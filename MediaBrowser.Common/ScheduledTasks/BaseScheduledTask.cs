﻿using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Kernel;
using MediaBrowser.Common.Serialization;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Common.ScheduledTasks
{
    /// <summary>
    /// Represents a task that can be executed at a scheduled time
    /// </summary>
    /// <typeparam name="TKernelType">The type of the T kernel type.</typeparam>
    public abstract class BaseScheduledTask<TKernelType> : IScheduledTask
        where TKernelType : IKernel
    {
        /// <summary>
        /// Gets the kernel.
        /// </summary>
        /// <value>The kernel.</value>
        protected TKernelType Kernel { get; private set; }

        /// <summary>
        /// The _last execution result
        /// </summary>
        private TaskResult _lastExecutionResult;
        /// <summary>
        /// The _last execution resultinitialized
        /// </summary>
        private bool _lastExecutionResultinitialized;
        /// <summary>
        /// The _last execution result sync lock
        /// </summary>
        private object _lastExecutionResultSyncLock = new object();
        /// <summary>
        /// Gets the last execution result.
        /// </summary>
        /// <value>The last execution result.</value>
        public TaskResult LastExecutionResult
        {
            get
            {
                LazyInitializer.EnsureInitialized(ref _lastExecutionResult, ref _lastExecutionResultinitialized, ref _lastExecutionResultSyncLock, () =>
                {
                    try
                    {
                        return JsonSerializer.DeserializeFromFile<TaskResult>(HistoryFilePath);
                    }
                    catch (IOException)
                    {
                        // File doesn't exist. No biggie
                        return null;
                    }
                });

                return _lastExecutionResult;
            }
            private set
            {
                _lastExecutionResult = value;

                _lastExecutionResultinitialized = value != null;
            }
        }

        /// <summary>
        /// The _scheduled tasks data directory
        /// </summary>
        private string _scheduledTasksDataDirectory;
        /// <summary>
        /// Gets the scheduled tasks data directory.
        /// </summary>
        /// <value>The scheduled tasks data directory.</value>
        private string ScheduledTasksDataDirectory
        {
            get
            {
                if (_scheduledTasksDataDirectory == null)
                {
                    _scheduledTasksDataDirectory = Path.Combine(Kernel.ApplicationPaths.DataPath, "ScheduledTasks");

                    if (!Directory.Exists(_scheduledTasksDataDirectory))
                    {
                        Directory.CreateDirectory(_scheduledTasksDataDirectory);
                    }
                }
                return _scheduledTasksDataDirectory;
            }
        }

        /// <summary>
        /// The _scheduled tasks configuration directory
        /// </summary>
        private string _scheduledTasksConfigurationDirectory;
        /// <summary>
        /// Gets the scheduled tasks configuration directory.
        /// </summary>
        /// <value>The scheduled tasks configuration directory.</value>
        private string ScheduledTasksConfigurationDirectory
        {
            get
            {
                if (_scheduledTasksConfigurationDirectory == null)
                {
                    _scheduledTasksConfigurationDirectory = Path.Combine(Kernel.ApplicationPaths.ConfigurationDirectoryPath, "ScheduledTasks");

                    if (!Directory.Exists(_scheduledTasksConfigurationDirectory))
                    {
                        Directory.CreateDirectory(_scheduledTasksConfigurationDirectory);
                    }
                }
                return _scheduledTasksConfigurationDirectory;
            }
        }

        /// <summary>
        /// Gets the configuration file path.
        /// </summary>
        /// <value>The configuration file path.</value>
        private string ConfigurationFilePath
        {
            get { return Path.Combine(ScheduledTasksConfigurationDirectory, Id + ".js"); }
        }

        /// <summary>
        /// Gets the history file path.
        /// </summary>
        /// <value>The history file path.</value>
        private string HistoryFilePath
        {
            get { return Path.Combine(ScheduledTasksDataDirectory, Id + ".js"); }
        }

        /// <summary>
        /// Gets the current cancellation token
        /// </summary>
        /// <value>The current cancellation token source.</value>
        private CancellationTokenSource CurrentCancellationTokenSource { get; set; }

        /// <summary>
        /// Gets or sets the current execution start time.
        /// </summary>
        /// <value>The current execution start time.</value>
        private DateTime CurrentExecutionStartTime { get; set; }

        /// <summary>
        /// Gets the state.
        /// </summary>
        /// <value>The state.</value>
        public TaskState State
        {
            get
            {
                if (CurrentCancellationTokenSource != null)
                {
                    return CurrentCancellationTokenSource.IsCancellationRequested
                               ? TaskState.Cancelling
                               : TaskState.Running;
                }

                return TaskState.Idle;
            }
        }

        /// <summary>
        /// Gets the current progress.
        /// </summary>
        /// <value>The current progress.</value>
        public TaskProgress CurrentProgress { get; private set; }

        /// <summary>
        /// The _triggers
        /// </summary>
        private IEnumerable<BaseTaskTrigger> _triggers;
        /// <summary>
        /// The _triggers initialized
        /// </summary>
        private bool _triggersInitialized;
        /// <summary>
        /// The _triggers sync lock
        /// </summary>
        private object _triggersSyncLock = new object();
        /// <summary>
        /// Gets the triggers that define when the task will run
        /// </summary>
        /// <value>The triggers.</value>
        /// <exception cref="System.ArgumentNullException">value</exception>
        public IEnumerable<BaseTaskTrigger> Triggers
        {
            get
            {
                LazyInitializer.EnsureInitialized(ref _triggers, ref _triggersInitialized, ref _triggersSyncLock, () =>
                {
                    try
                    {
                        return JsonSerializer.DeserializeFromFile<IEnumerable<TaskTriggerInfo>>(ConfigurationFilePath)
                            .Select(t => ScheduledTaskHelpers.GetTrigger(t, Kernel))
                            .ToList();
                    }
                    catch (IOException)
                    {
                        // File doesn't exist. No biggie. Return defaults.
                        return GetDefaultTriggers();
                    }
                });

                return _triggers;
            }
            set
            {
                if (value == null)
                {
                    throw new ArgumentNullException("value");
                }

                // Cleanup current triggers
                if (_triggers != null)
                {
                    DisposeTriggers();
                }

                _triggers = value.ToList();

                _triggersInitialized = true;

                ReloadTriggerEvents();

                JsonSerializer.SerializeToFile(_triggers.Select(ScheduledTaskHelpers.GetTriggerInfo), ConfigurationFilePath);
            }
        }

        /// <summary>
        /// Creates the triggers that define when the task will run
        /// </summary>
        /// <returns>IEnumerable{BaseTaskTrigger}.</returns>
        protected abstract IEnumerable<BaseTaskTrigger> GetDefaultTriggers();

        /// <summary>
        /// Returns the task to be executed
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <param name="progress">The progress.</param>
        /// <returns>Task.</returns>
        protected abstract Task ExecuteInternal(CancellationToken cancellationToken, IProgress<TaskProgress> progress);

        /// <summary>
        /// Gets the name of the task
        /// </summary>
        /// <value>The name.</value>
        public abstract string Name { get; }

        /// <summary>
        /// Gets the description.
        /// </summary>
        /// <value>The description.</value>
        public abstract string Description { get; }

        /// <summary>
        /// Gets the category.
        /// </summary>
        /// <value>The category.</value>
        public virtual string Category
        {
            get { return "Application"; }
        }

        /// <summary>
        /// The _id
        /// </summary>
        private Guid? _id;
        /// <summary>
        /// Gets the unique id.
        /// </summary>
        /// <value>The unique id.</value>
        public Guid Id
        {
            get
            {
                if (!_id.HasValue)
                {
                    _id = GetType().FullName.GetMD5();
                }

                return _id.Value;
            }
        }

        /// <summary>
        /// Gets the logger.
        /// </summary>
        /// <value>The logger.</value>
        protected ILogger Logger { get; private set; }

        /// <summary>
        /// Initializes the specified kernel.
        /// </summary>
        /// <param name="kernel">The kernel.</param>
        /// <param name="logger">The logger.</param>
        public void Initialize(IKernel kernel, ILogger logger)
        {
            Logger = logger;
            
            Kernel = (TKernelType)kernel;
            ReloadTriggerEvents();
        }

        /// <summary>
        /// Reloads the trigger events.
        /// </summary>
        private void ReloadTriggerEvents()
        {
            foreach (var trigger in Triggers)
            {
                trigger.Stop();

                trigger.Triggered -= trigger_Triggered;
                trigger.Triggered += trigger_Triggered;
                trigger.Start();
            }
        }

        /// <summary>
        /// Handles the Triggered event of the trigger control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="EventArgs" /> instance containing the event data.</param>
        void trigger_Triggered(object sender, EventArgs e)
        {
            var trigger = (BaseTaskTrigger)sender;

            Logger.Info("{0} fired for task: {1}", trigger.GetType().Name, Name);

            Kernel.TaskManager.QueueScheduledTask(this);
        }

        /// <summary>
        /// Executes the task
        /// </summary>
        /// <returns>Task.</returns>
        /// <exception cref="System.InvalidOperationException">Cannot execute a Task that is already running</exception>
        public async Task Execute()
        {
            // Cancel the current execution, if any
            if (CurrentCancellationTokenSource != null)
            {
                throw new InvalidOperationException("Cannot execute a Task that is already running");
            }

            CurrentCancellationTokenSource = new CancellationTokenSource();

            Logger.Info("Executing {0}", Name);

            var progress = new Progress<TaskProgress>();

            progress.ProgressChanged += progress_ProgressChanged;

            TaskCompletionStatus status;
            CurrentExecutionStartTime = DateTime.UtcNow;

            Kernel.TcpManager.SendWebSocketMessage("ScheduledTaskBeginExecute", Name);

            try
            {
                await Task.Run(async () => await ExecuteInternal(CurrentCancellationTokenSource.Token, progress).ConfigureAwait(false)).ConfigureAwait(false);

                status = TaskCompletionStatus.Completed;
            }
            catch (OperationCanceledException)
            {
                status = TaskCompletionStatus.Cancelled;
            }
            catch (Exception ex)
            {
                Logger.ErrorException("Error", ex);

                status = TaskCompletionStatus.Failed;
            }

            var endTime = DateTime.UtcNow;

            LogResult(endTime, status);

            Kernel.TcpManager.SendWebSocketMessage("ScheduledTaskEndExecute", LastExecutionResult);

            progress.ProgressChanged -= progress_ProgressChanged;
            CurrentCancellationTokenSource.Dispose();
            CurrentCancellationTokenSource = null;
            CurrentProgress = null;

            Kernel.TaskManager.OnTaskCompleted(this);
        }

        /// <summary>
        /// Logs the result.
        /// </summary>
        /// <param name="endTime">The end time.</param>
        /// <param name="status">The status.</param>
        private void LogResult(DateTime endTime, TaskCompletionStatus status)
        {
            var startTime = CurrentExecutionStartTime;
            var elapsedTime = endTime - startTime;
            
            Logger.Info("{0} {1} after {2} minute(s) and {3} seconds", Name, status, Math.Truncate(elapsedTime.TotalMinutes), elapsedTime.Seconds);

            var result = new TaskResult
            {
                StartTimeUtc = startTime,
                EndTimeUtc = endTime,
                Status = status,
                Name = Name,
                Id = Id
            };

            JsonSerializer.SerializeToFile(result, HistoryFilePath);

            LastExecutionResult = result;
        }

        /// <summary>
        /// Progress_s the progress changed.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="e">The e.</param>
        void progress_ProgressChanged(object sender, TaskProgress e)
        {
            CurrentProgress = e;
        }

        /// <summary>
        /// Stops the task if it is currently executing
        /// </summary>
        /// <exception cref="System.InvalidOperationException">Cannot cancel a Task unless it is in the Running state.</exception>
        public void Cancel()
        {
            if (State != TaskState.Running)
            {
                throw new InvalidOperationException("Cannot cancel a Task unless it is in the Running state.");
            }

            CancelIfRunning();
        }

        /// <summary>
        /// Cancels if running.
        /// </summary>
        public void CancelIfRunning()
        {
            if (State == TaskState.Running)
            {
                Logger.Info("Attempting to cancel Scheduled Task {0}", Name);
                CurrentCancellationTokenSource.Cancel();
            }
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="dispose"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool dispose)
        {
            if (dispose)
            {
                DisposeTriggers();

                if (State == TaskState.Running)
                {
                    LogResult(DateTime.UtcNow, TaskCompletionStatus.Aborted);
                }

                if (CurrentCancellationTokenSource != null)
                {
                    CurrentCancellationTokenSource.Dispose();
                }
            }
        }

        /// <summary>
        /// Disposes each trigger
        /// </summary>
        private void DisposeTriggers()
        {
            foreach (var trigger in Triggers)
            {
                trigger.Triggered -= trigger_Triggered;
                trigger.Dispose();
            }
        }
    }
}
