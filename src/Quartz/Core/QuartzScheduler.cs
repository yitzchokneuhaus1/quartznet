#region License
/* 
 * Copyright 2001-2009 Terracotta, Inc. 
 * 
 * Licensed under the Apache License, Version 2.0 (the "License"); you may not 
 * use this file except in compliance with the License. You may obtain a copy 
 * of the License at 
 * 
 *   http://www.apache.org/licenses/LICENSE-2.0 
 *   
 * Unless required by applicable law or agreed to in writing, software 
 * distributed under the License is distributed on an "AS IS" BASIS, WITHOUT 
 * WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the 
 * License for the specific language governing permissions and limitations 
 * under the License.
 * 
 */
#endregion

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.Remoting;
using System.Text;
using System.Threading;

using Common.Logging;

using Quartz.Impl;
using Quartz.Listener;
using Quartz.Simpl;
using Quartz.Spi;
using Quartz.Util;

namespace Quartz.Core
{
    /// <summary>
    /// This is the heart of Quartz, an indirect implementation of the <see cref="IScheduler" />
    /// interface, containing methods to schedule <see cref="IJob" />s,
    /// register <see cref="IJobListener" /> instances, etc.
    /// </summary>
    /// <seealso cref="IScheduler" />
    /// <seealso cref="QuartzSchedulerThread" />
    /// <seealso cref="IJobStore" />
    /// <seealso cref="IThreadPool" />
    /// <author>James House</author>
    /// <author>Marko Lahma (.NET)</author>
    public class QuartzScheduler : MarshalByRefObject, IRemotableQuartzScheduler
    {
        private readonly ILog log;
        private static readonly FileVersionInfo versionInfo;

        private readonly QuartzSchedulerResources resources;

        private readonly QuartzSchedulerThread schedThread;
        private readonly SchedulerContext context = new SchedulerContext();

        private readonly IDictionary<string, IJobListener> globalJobListeners = new Dictionary<string, IJobListener>(10);
        private readonly IDictionary<string, ITriggerListener> globalTriggerListeners = new Dictionary<string, ITriggerListener>(10);
        private readonly IList<ISchedulerListener> schedulerListeners = new List<ISchedulerListener>(10);
        
        private IDictionary<string, IJobListener> internalJobListeners = new Dictionary<string, IJobListener>(10);
        private IDictionary<string, ITriggerListener> internalTriggerListeners = new Dictionary<String, ITriggerListener>(10);
        private IList<ISchedulerListener> internalSchedulerListeners = new List<ISchedulerListener>(10);

        private IJobFactory jobFactory = new SimpleJobFactory();
        private readonly ExecutingJobsManager jobMgr;
        private readonly ErrorLogger errLogger;
        private readonly ISchedulerSignaler signaler;
        private readonly Random random = new Random();
        private readonly List<object> holdToPreventGC = new List<object>(5);
        private bool signalOnSchedulingChange = true;
        private volatile bool closed;
        private volatile bool shuttingDown;
        private DateTimeOffset? initialStart;
        private bool boundRemotely;

        /// <summary>
        /// Initializes the <see cref="QuartzScheduler"/> class.
        /// </summary>
        static QuartzScheduler()
        {
            Assembly asm = Assembly.GetAssembly(typeof(QuartzScheduler));
            if (asm != null)
            {
                versionInfo = FileVersionInfo.GetVersionInfo(asm.Location);
            }
        }

        /// <summary>
        /// Gets the version of the Quartz Scheduler.
        /// </summary>
        /// <value>The version.</value>
        public string Version
        {
            get { return versionInfo.FileVersion; }
        }

        /// <summary>
        /// Gets the version major.
        /// </summary>
        /// <value>The version major.</value>
        public static string VersionMajor
        {
            get { return versionInfo.FileMajorPart.ToString(CultureInfo.InvariantCulture); }
        }

        /// <summary>
        /// Gets the version minor.
        /// </summary>
        /// <value>The version minor.</value>
        public static string VersionMinor
        {
            get { return versionInfo.FileMinorPart.ToString(CultureInfo.InvariantCulture); }
        }

        /// <summary>
        /// Gets the version iteration.
        /// </summary>
        /// <value>The version iteration.</value>
        public static string VersionIteration
        {
            get { return versionInfo.FileBuildPart.ToString(CultureInfo.InvariantCulture); }
        }

        /// <summary>
        /// Gets the scheduler signaler.
        /// </summary>
        /// <value>The scheduler signaler.</value>
        public virtual ISchedulerSignaler SchedulerSignaler
        {
            get { return signaler; }
        }

        /// <summary>
        /// Returns the name of the <see cref="QuartzScheduler" />.
        /// </summary>
        public virtual string SchedulerName
        {
            get { return resources.Name; }
        }

        /// <summary> 
        /// Returns the instance Id of the <see cref="QuartzScheduler" />.
        /// </summary>
        public virtual string SchedulerInstanceId
        {
            get { return resources.InstanceId; }
        }


        /// <summary>
        /// Returns the <see cref="SchedulerContext" /> of the <see cref="IScheduler" />.
        /// </summary>
        public virtual SchedulerContext SchedulerContext
        {
            get { return context; }
        }

        /// <summary>
        /// Gets or sets a value indicating whether to signal on scheduling change.
        /// </summary>
        /// <value>
        /// 	<c>true</c> if schduler should signal on scheduling change; otherwise, <c>false</c>.
        /// </value>
        public virtual bool SignalOnSchedulingChange
        {
            get { return signalOnSchedulingChange; }
            set { signalOnSchedulingChange = value; }
        }

        /// <summary>
        /// Reports whether the <see cref="IScheduler" /> is paused.
        /// </summary>
        public virtual bool InStandbyMode
        {
            get { return schedThread.Paused; }
        }

        /// <summary>
        /// Gets the job store class.
        /// </summary>
        /// <value>The job store class.</value>
        public virtual Type JobStoreClass
        {
            get { return resources.JobStore.GetType(); }
        }

        /// <summary>
        /// Gets the thread pool class.
        /// </summary>
        /// <value>The thread pool class.</value>
        public virtual Type ThreadPoolClass
        {
            get { return resources.ThreadPool.GetType(); }
        }

        /// <summary>
        /// Gets the size of the thread pool.
        /// </summary>
        /// <value>The size of the thread pool.</value>
        public virtual int ThreadPoolSize
        {
            get { return resources.ThreadPool.PoolSize; }
        }

        /// <summary>
        /// Reports whether the <see cref="IScheduler" /> has been Shutdown.
        /// </summary>
        public virtual bool IsShutdown
        {
            get { return closed; }
        }


        public virtual bool IsShuttingDown
        {
            get { return shuttingDown; }
        }

        public virtual bool IsStarted
        {
            get { return !shuttingDown && !closed && !InStandbyMode && initialStart != null; }
        }

        /// <summary>
        /// Return a list of <see cref="JobExecutionContext" /> objects that
        /// represent all currently executing Jobs in this Scheduler instance.
        /// <p>
        /// This method is not cluster aware.  That is, it will only return Jobs
        /// currently executing in this Scheduler instance, not across the entire
        /// cluster.
        /// </p>
        /// <p>
        /// Note that the list returned is an 'instantaneous' snap-shot, and that as
        /// soon as it's returned, the true list of executing jobs may be different.
        /// </p>
        /// </summary>
        public virtual IList<JobExecutionContext> CurrentlyExecutingJobs
        {
            get { return jobMgr.ExecutingJobs; }
        }

        /// <summary>
        /// Get a List containing all of the <see cref="IJobListener" />
        /// s in the <see cref="IScheduler" />'s <i>global</i> list.
        /// </summary>
        public virtual IList<IJobListener> GlobalJobListeners
        {
            get { return new List<IJobListener>(globalJobListeners.Values); }
        }

        /// <summary>
        /// Get the <i>global</i><see cref="IJobListener" />
        /// that has the given name.
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public IJobListener GetGlobalJobListener(string name)
        {
            lock (globalJobListeners)
            {
                IJobListener jobListener;
                globalJobListeners.TryGetValue(name, out jobListener);
                return jobListener;
            }
        }

        /// <summary>
        /// Get a list containing all of the <see cref="ITriggerListener" />s 
        /// in the <see cref="IScheduler" />'s <i>global</i> list.
        /// </summary>
        public virtual IList<ITriggerListener> GlobalTriggerListeners
        {
            get 
            { 
                lock (globalTriggerListeners)
                {
                    return new List<ITriggerListener>(globalTriggerListeners.Values);
                } 
            }
        }

        /// <summary>
        /// Get a list containing all of the <see cref="ISchedulerListener" />s
        /// registered with the <see cref="IScheduler" />.
        /// </summary>
        public virtual IList<ISchedulerListener> SchedulerListeners
        {
            get
            {
                lock (schedulerListeners)
                {
                    return new List<ISchedulerListener>(schedulerListeners);
                }
            }
        }

        /// <summary>
        /// Register the given <see cref="ISchedulerListener" /> with the
        /// <see cref="IScheduler" />'s list of internal listeners.
        /// </summary>
        /// <param name="schedulerListener"></param>
        public void AddInternalSchedulerListener(ISchedulerListener schedulerListener)
        {
            lock (internalSchedulerListeners)
            {
                internalSchedulerListeners.Add(schedulerListener);
            }
        }

        /// <summary>
        /// Remove the given <see cref="ISchedulerListener" /> from the
        /// <see cref="IScheduler" />'s list of internal listeners.
        /// </summary>
        /// <param name="schedulerListener"></param>
        /// <returns>true if the identified listener was found in the list, andremoved.</returns>
        public bool RemoveInternalSchedulerListener(ISchedulerListener schedulerListener)
        {
            lock (internalSchedulerListeners)
            {
                return internalSchedulerListeners.Remove(schedulerListener);
            }
        }

        /// <summary>
        /// Get a List containing all of the <i>internal</i> <see cref="ISchedulerListener" />s
        /// registered with the <see cref="IScheduler" />.
        /// </summary>
        public IList<ISchedulerListener> InternalSchedulerListeners
        {
            get
            {
                lock (internalSchedulerListeners)
                {
                    return new List<ISchedulerListener>(internalSchedulerListeners).AsReadOnly();
                }
            }
        }

        /// <summary>
        /// Gets or sets the job factory.
        /// </summary>
        /// <value>The job factory.</value>
        public virtual IJobFactory JobFactory
        {
            get { return jobFactory; }
            set
            {
                if (value == null)
                {
                    throw new ArgumentException("JobFactory cannot be set to null!");
                }

                log.Info("JobFactory set to: " + value);

                jobFactory = value;
            }
        }



        /// <summary>
        /// Create a <see cref="QuartzScheduler" /> with the given configuration
        /// properties.
        /// </summary>
        /// <seealso cref="QuartzSchedulerResources" />
        public QuartzScheduler(QuartzSchedulerResources resources, TimeSpan idleWaitTime, TimeSpan dbRetryInterval)
        {
            log = LogManager.GetLogger(GetType());
            this.resources = resources;
            try
            {
                Bind();
            }
            catch (Exception re)
            {
                throw new SchedulerException("Unable to bind scheduler to remoting context.", re);
            }

            schedThread = new QuartzSchedulerThread(this, resources);
            if (idleWaitTime > TimeSpan.Zero)
            {
                schedThread.IdleWaitTime = idleWaitTime;
            }
            if (dbRetryInterval > TimeSpan.Zero)
            {
                schedThread.DbFailureRetryInterval = dbRetryInterval;
            }

            jobMgr = new ExecutingJobsManager();
            AddGlobalJobListener(jobMgr);
            errLogger = new ErrorLogger();
            AddSchedulerListener(errLogger);

            signaler = new SchedulerSignalerImpl(this, schedThread);

            log.InfoFormat(CultureInfo.InvariantCulture, "Quartz Scheduler v.{0} created.", Version);


            log.Info("Scheduler meta-data: " +
                    (new SchedulerMetaData(SchedulerName,
                                           SchedulerInstanceId, GetType(), boundRemotely, RunningSince != null,
                                           InStandbyMode, IsShutdown, RunningSince,
                                           NumJobsExecuted, JobStoreClass, 
                                           SupportsPersistence, Clustered, ThreadPoolClass,
                                           ThreadPoolSize, Version)));
        }

        /// <summary>
        /// Bind the scheduler to remoting infrastructure.
        /// </summary>
        private void Bind()
        {
            if (resources.SchedulerExporter != null)
            {
                resources.SchedulerExporter.Bind(this);
                boundRemotely = true;
            }
        }

        /// <summary>
        /// Un-bind the scheduler from remoting infrastructure.
        /// </summary>
        private void UnBind()
        {
            if (resources.SchedulerExporter != null)
            {
                resources.SchedulerExporter.UnBind(this);
            }
        }

        /// <summary>
        /// Adds an object that should be kept as reference to prevent
        /// it from being garbage collected.
        /// </summary>
        /// <param name="obj">The obj.</param>
        public virtual void AddNoGCObject(object obj)
        {
            holdToPreventGC.Add(obj);
        }

        /// <summary>
        /// Removes the object from garbae collection protected list.
        /// </summary>
        /// <param name="obj">The obj.</param>
        /// <returns></returns>
        public virtual bool RemoveNoGCObject(object obj)
        {
            return holdToPreventGC.Remove(obj);
        }

        /// <summary>
        /// Starts the <see cref="QuartzScheduler" />'s threads that fire <see cref="Trigger" />s.
        /// <p>
        /// All <see cref="Trigger" />s that have misfired will
        /// be passed to the appropriate TriggerListener(s).
        /// </p>
        /// </summary>
        public virtual void Start()
        {
            if (shuttingDown || closed)
            {
                throw new SchedulerException("The Scheduler cannot be restarted after Shutdown() has been called.");
            }

            if (!initialStart.HasValue)
            {
                initialStart = SystemTime.UtcNow();
                resources.JobStore.SchedulerStarted();
                StartPlugins();
            }

            schedThread.TogglePause(false);

            log.Info(string.Format(CultureInfo.InvariantCulture, "Scheduler {0} started.", resources.GetUniqueIdentifier()));

            NotifySchedulerListenersStarted();
        }

        public void StartDelayed(TimeSpan delay)
        {
            if (shuttingDown || closed) 
            {
                throw new SchedulerException(
                        "The Scheduler cannot be restarted after Shutdown() has been called.");
            }

            DelayedSchedulerStarter starter = new DelayedSchedulerStarter(this, delay, log);
            Thread t = new Thread(starter.Run);
            t.Start();
        }

        /// <summary>
        /// Helper class to start scheduler in a delayed fashion.
        /// </summary>
        private class DelayedSchedulerStarter
        {
            private readonly QuartzScheduler scheduler;
            private readonly TimeSpan delay;
            private readonly ILog logger;

            public DelayedSchedulerStarter(QuartzScheduler scheduler, TimeSpan delay, ILog logger)
            {
                this.scheduler = scheduler;
                this.delay = delay;
                this.logger = logger;
            }

            public void Run()
            {
                try
                {
                    Thread.Sleep(delay);
                }
                catch (ThreadInterruptedException) { }
                try
                {
                    scheduler.Start();
                }
                catch (SchedulerException se)
                {
                    logger.Error("Unable to start secheduler after startup delay.", se);
                }
            }
        }

        /// <summary>
        /// Temporarily halts the <see cref="QuartzScheduler" />'s firing of <see cref="Trigger" />s.
        /// <p>
        /// The scheduler is not destroyed, and can be re-started at any time.
        /// </p>
        /// </summary>
        public virtual void Standby()
        {
            schedThread.TogglePause(true);
            log.Info(string.Format(CultureInfo.InvariantCulture, "Scheduler {0} paused.", resources.GetUniqueIdentifier()));
            NotifySchedulerListenersInStandbyMode();        
        }

        /// <summary>
        /// Gets the running since.
        /// </summary>
        /// <value>The running since.</value>
        public virtual DateTimeOffset? RunningSince
        {
            get { return initialStart; }
        }

        /// <summary>
        /// Gets the number of jobs executed.
        /// </summary>
        /// <value>The number of jobs executed.</value>
        public virtual int NumJobsExecuted
        {
            get { return jobMgr.NumJobsFired; }
        }

        /// <summary>
        /// Gets a value indicating whether this scheduler supports persistence.
        /// </summary>
        /// <value><c>true</c> if supports persistence; otherwise, <c>false</c>.</value>
        public virtual bool SupportsPersistence
        {
            get { return resources.JobStore.SupportsPersistence; }
        }

        public bool Clustered
        {
            get { return resources.JobStore.Clustered; }
        }

        /// <summary>
        /// Halts the <see cref="QuartzScheduler" />'s firing of <see cref="Trigger" />s,
        /// and cleans up all resources associated with the QuartzScheduler.
        /// Equivalent to <see cref="Shutdown(bool)" />.
        /// <p>
        /// The scheduler cannot be re-started.
        /// </p>
        /// </summary>
        public virtual void Shutdown()
        {
            Shutdown(false);
        }

        /// <summary>
        /// Halts the <see cref="QuartzScheduler" />'s firing of <see cref="Trigger" />s,
        /// and cleans up all resources associated with the QuartzScheduler.
        /// <p>
        /// The scheduler cannot be re-started.
        /// </p>
        /// </summary>
        /// <param name="waitForJobsToComplete">
        /// if <see langword="true" /> the scheduler will not allow this method
        /// to return until all currently executing jobs have completed.
        /// </param>
        public virtual void Shutdown(bool waitForJobsToComplete)
        {
            if (shuttingDown || closed)
            {
                return;
            }

            shuttingDown = true;

            log.Info(string.Format(CultureInfo.InvariantCulture, "Scheduler {0} shutting down.", resources.GetUniqueIdentifier()));

            Standby();

            schedThread.Halt();

            NotifySchedulerListenersShuttingdown();

            if((resources.InterruptJobsOnShutdown && !waitForJobsToComplete) || (resources.InterruptJobsOnShutdownWithWait && waitForJobsToComplete))
            {
                IList<JobExecutionContext> jobs = CurrentlyExecutingJobs;
                foreach (JobExecutionContext job in jobs) 
                {
                    if(job.JobInstance is IInterruptableJob)
                    {
                        try 
                        {
                            ((IInterruptableJob) job.JobInstance).Interrupt();
                        } 
                        catch (Exception ex) 
                        {
                            // do nothing, this was just a courtesy effort
                            log.WarnFormat("Encountered error when interrupting job {0} during shutdown: {1}", job.JobDetail.FullName, ex);
                        }
                    }
                }
            }
        
            resources.ThreadPool.Shutdown(waitForJobsToComplete);

            if (waitForJobsToComplete)
            {
                while (jobMgr.NumJobsCurrentlyExecuting > 0)
                {
                    try
                    {
                        Thread.Sleep(100);
                    }
                    catch (ThreadInterruptedException)
                    {
                    }
                }
            }

            // Scheduler thread may have be waiting for the fire time of an acquired 
            // trigger and need time to release the trigger once halted, so make sure
            // the thread is dead before continuing to shutdown the job store.
            try
            {
                schedThread.Join();
            }
            catch (ThreadInterruptedException)
            {
            }

            closed = true;

            resources.JobStore.Shutdown();

            NotifySchedulerListenersShutdown();

            ShutdownPlugins();

            SchedulerRepository.Instance.Remove(resources.Name);

            holdToPreventGC.Clear();

            try
            {
                UnBind();
            }
            catch (RemotingException)
            {
            }

            log.Info(string.Format(CultureInfo.InvariantCulture, "Scheduler {0} Shutdown complete.", resources.GetUniqueIdentifier()));
        }

        /// <summary>
        /// Validates the state.
        /// </summary>
        public virtual void ValidateState()
        {
            if (IsShutdown)
            {
                throw new SchedulerException("The Scheduler has been Shutdown.");
            }

            // other conditions to check (?)
        }

        /// <summary> 
        /// Add the <see cref="IJob" /> identified by the given
        /// <see cref="JobDetail" /> to the Scheduler, and
        /// associate the given <see cref="Trigger" /> with it.
        /// <p>
        /// If the given Trigger does not reference any <see cref="IJob" />, then it
        /// will be set to reference the Job passed with it into this method.
        /// </p>
        /// </summary>
        public virtual DateTimeOffset ScheduleJob(JobDetail jobDetail, Trigger trigger)
        {
            ValidateState();


            if (jobDetail == null)
            {
                throw new SchedulerException("JobDetail cannot be null");
            }

            if (trigger == null)
            {
                throw new SchedulerException("Trigger cannot be null");
            }

            jobDetail.Validate();

            if (trigger.JobName == null)
            {
                trigger.JobName = jobDetail.Name;
                trigger.JobGroup = jobDetail.Group;
            }
            else if (trigger.JobName != null && !trigger.JobName.Equals(jobDetail.Name))
            {
                throw new SchedulerException("Trigger does not reference given job!");
            }
            else if (trigger.JobGroup != null && !trigger.JobGroup.Equals(jobDetail.Group))
            {
                throw new SchedulerException("Trigger does not reference given job!");
            }

            trigger.Validate();

            ICalendar cal = null;
            if (trigger.CalendarName != null)
            {
                cal = resources.JobStore.RetrieveCalendar(trigger.CalendarName);
                if (cal == null)
                {
                    throw new SchedulerException(string.Format(CultureInfo.InvariantCulture, "Calendar not found: {0}", trigger.CalendarName));
                }
            }

            DateTimeOffset? ft = trigger.ComputeFirstFireTimeUtc(cal);

            if (!ft.HasValue)
            {
                throw new SchedulerException("Based on configured schedule, the given trigger will never fire.");
            }

            resources.JobStore.StoreJobAndTrigger(jobDetail, trigger);
            NotifySchedulerListenersJobAdded(jobDetail);
            NotifySchedulerThread(trigger.GetNextFireTimeUtc());
            NotifySchedulerListenersScheduled(trigger);

            return ft.Value;
        }

        /// <summary>
        /// Schedule the given <see cref="Trigger" /> with the
        /// <see cref="IJob" /> identified by the <see cref="Trigger" />'s settings.
        /// </summary>
        public virtual DateTimeOffset ScheduleJob(Trigger trigger)
        {
            ValidateState();

            if (trigger == null)
            {
                throw new SchedulerException("Trigger cannot be null");
            }

            trigger.Validate();

            ICalendar cal = null;
            if (trigger.CalendarName != null)
            {
                cal = resources.JobStore.RetrieveCalendar(trigger.CalendarName);
                if (cal == null)
                {
                    throw new SchedulerException(string.Format(CultureInfo.InvariantCulture, "Calendar not found: {0}", trigger.CalendarName));
                }
            }

            DateTimeOffset? ft = trigger.ComputeFirstFireTimeUtc(cal);

            if (!ft.HasValue)
            {
                throw new SchedulerException("Based on configured schedule, the given trigger will never fire.");
            }

            resources.JobStore.StoreTrigger(trigger, false);
            NotifySchedulerThread(trigger.GetNextFireTimeUtc());
            NotifySchedulerListenersScheduled(trigger);

            return ft.Value;
        }

        /// <summary>
        /// Add the given <see cref="IJob" /> to the Scheduler - with no associated
        /// <see cref="Trigger" />. The <see cref="IJob" /> will be 'dormant' until
        /// it is scheduled with a <see cref="Trigger" />, or <see cref="IScheduler.TriggerJob(string ,string)" />
        /// is called for it.
        /// <p>
        /// The <see cref="IJob" /> must by definition be 'durable', if it is not,
        /// SchedulerException will be thrown.
        /// </p>
        /// </summary>
        public virtual void AddJob(JobDetail jobDetail, bool replace)
        {
            ValidateState();

            if (!jobDetail.Durable && !replace)
            {
                throw new SchedulerException("Jobs added with no trigger must be durable.");
            }

            resources.JobStore.StoreJob(jobDetail, replace);
            NotifySchedulerThread(null);
            NotifySchedulerListenersJobAdded(jobDetail);
        }

        /// <summary>
        /// Delete the identified <see cref="IJob" /> from the Scheduler - and any
        /// associated <see cref="Trigger" />s.
        /// </summary>
        /// <returns> true if the Job was found and deleted.</returns>
        public virtual bool DeleteJob(string jobName, string groupName)
        {
            ValidateState();

            if (groupName == null)
            {
                groupName = SchedulerConstants.DefaultGroup;
            }

            bool result = false;
            IList<Trigger> triggers = GetTriggersOfJob(jobName, groupName);
            foreach (Trigger trigger in triggers)
            {
                if (!UnscheduleJob(trigger.Name, trigger.Group))
                {
                    StringBuilder sb = new StringBuilder()
                        .Append("Unable to unschedule trigger [")
                        .Append(trigger.Key).Append("] while deleting job [")
                        .Append(groupName).Append(".").Append(jobName).Append("]");
                    throw new SchedulerException(sb.ToString());
                }
                result = true;
            }

            result = resources.JobStore.RemoveJob(jobName, groupName) || result;
            if (result)
            {
                NotifySchedulerThread(null);
                NotifySchedulerListenersJobDeleted(jobName, groupName);
            }
            return result;
        }

        /// <summary>
        /// Remove the indicated <see cref="Trigger" /> from the
        /// scheduler.
        /// </summary>
        public virtual bool UnscheduleJob(string triggerName, string groupName)
        {
            ValidateState();

            if (groupName == null)
            {
                groupName = SchedulerConstants.DefaultGroup;
            }

            if (resources.JobStore.RemoveTrigger(triggerName, groupName))
            {
                NotifySchedulerThread(null);
                NotifySchedulerListenersUnscheduled(triggerName, groupName);
            }
            else
            {
                return false;
            }

            return true;
        }


        /// <summary>
        /// Remove (delete) the <see cref="Trigger" /> with the
        /// given name, and store the new given one - which must be associated
        /// with the same job.
        /// </summary>
        /// <param name="triggerName">The name of the <see cref="Trigger" /> to be removed.</param>
        /// <param name="groupName">The group name of the <see cref="Trigger" /> to be removed.</param>
        /// <param name="newTrigger">The new <see cref="Trigger" /> to be stored.</param>
        /// <returns>
        /// 	<see langword="null" /> if a <see cref="Trigger" /> with the given
        /// name and group was not found and removed from the store, otherwise
        /// the first fire time of the newly scheduled trigger.
        /// </returns>
        public virtual DateTimeOffset? RescheduleJob(string triggerName, string groupName, Trigger newTrigger)
        {
            ValidateState();

            if (groupName == null)
            {
                groupName = SchedulerConstants.DefaultGroup;
            }

            newTrigger.Validate();

            ICalendar cal = null;
            if (newTrigger.CalendarName != null)
            {
                cal = resources.JobStore.RetrieveCalendar(newTrigger.CalendarName);
            }

            DateTimeOffset? ft = newTrigger.ComputeFirstFireTimeUtc(cal);

            if (!ft.HasValue)
            {
                throw new SchedulerException("Based on configured schedule, the given trigger will never fire.");
            }

            if (resources.JobStore.ReplaceTrigger(triggerName, groupName, newTrigger))
            {
                NotifySchedulerThread(newTrigger.GetNextFireTimeUtc());
                NotifySchedulerListenersUnscheduled(triggerName, groupName);
                NotifySchedulerListenersScheduled(newTrigger);
            }
            else
            {
                return null;
            }

            return ft;
        }


        private string NewTriggerId()
        {
            long r = NextLong(random);
            if (r < 0)
            {
                r = -r;
            }
            return "MT_" + Convert.ToString(r, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Creates a new positive random number 
        /// </summary>
        /// <param name="random">The last random obtained</param>
        /// <returns>Returns a new positive random number</returns>
        public static long NextLong(Random random)
        {
            long temporaryLong = random.Next();
            temporaryLong = (temporaryLong << 32) + random.Next();
            if (random.Next(-1, 1) < 0)
            {
                return -temporaryLong;
            }
            
            return temporaryLong;
        }

        /// <summary>
        /// Trigger the identified <see cref="IJob" /> (Execute it now) - with a non-volatile trigger.
        /// </summary>
        public virtual void TriggerJob(string jobName, string groupName, JobDataMap data)
        {
            ValidateState();

            if (groupName == null)
            {
                groupName = SchedulerConstants.DefaultGroup;
            }

            Trigger trig =
                new SimpleTrigger(NewTriggerId(), SchedulerConstants.DefaultManualTriggers, jobName, groupName, SystemTime.UtcNow(),
                                  null, 0, TimeSpan.Zero);
            trig.Volatile = false;
            trig.ComputeFirstFireTimeUtc(null);
            if (data != null)
            {
                trig.JobDataMap = data;
            }

            bool collision = true;
            while (collision)
            {
                try
                {
                    resources.JobStore.StoreTrigger(trig, false);
                    collision = false;
                }
                catch (ObjectAlreadyExistsException)
                {
                    trig.Name = NewTriggerId();
                }
            }

            NotifySchedulerThread(trig.GetNextFireTimeUtc());
            NotifySchedulerListenersScheduled(trig);
        }

        /// <summary>
        /// Trigger the identified <see cref="IJob" /> (Execute it
        /// now) - with a volatile trigger.
        /// </summary>
        public virtual void TriggerJobWithVolatileTrigger(string jobName, string groupName,
                                                          JobDataMap data)
        {
            ValidateState();

            if (groupName == null)
            {
                groupName = SchedulerConstants.DefaultGroup;
            }

            Trigger trig =
                new SimpleTrigger(NewTriggerId(), SchedulerConstants.DefaultManualTriggers, jobName, groupName, SystemTime.UtcNow(),
                                  null, 0, TimeSpan.Zero);
            trig.Volatile = true;
            trig.ComputeFirstFireTimeUtc(null);
            if (data != null)
            {
                trig.JobDataMap = data;
            }

            bool collision = true;
            while (collision)
            {
                try
                {
                    resources.JobStore.StoreTrigger(trig, false);
                    collision = false;
                }
                catch (ObjectAlreadyExistsException)
                {
                    trig.Name = NewTriggerId();
                }
            }

            NotifySchedulerThread(trig.GetNextFireTimeUtc());
            NotifySchedulerListenersScheduled(trig);
        }

        /// <summary>
        /// Pause the <see cref="Trigger" /> with the given name.
        /// </summary>
        public virtual void PauseTrigger(string triggerName, string groupName)
        {
            ValidateState();

            if (groupName == null)
            {
                groupName = SchedulerConstants.DefaultGroup;
            }

            resources.JobStore.PauseTrigger(triggerName, groupName);
            NotifySchedulerThread(null);
            NotifySchedulerListenersPausedTrigger(triggerName, groupName);
        }

        /// <summary>
        /// Pause all of the <see cref="Trigger" />s in the given group.
        /// </summary>
        public virtual void PauseTriggerGroup(string groupName)
        {
            ValidateState();

            if (groupName == null)
            {
                groupName = SchedulerConstants.DefaultGroup;
            }

            resources.JobStore.PauseTriggerGroup(groupName);
            NotifySchedulerThread(null);
            NotifySchedulerListenersPausedTrigger(null, groupName);
        }

        /// <summary> 
        /// Pause the <see cref="JobDetail" /> with the given
        /// name - by pausing all of its current <see cref="Trigger" />s.
        /// </summary>
        public virtual void PauseJob(string jobName, string groupName)
        {
            ValidateState();

            if (groupName == null)
            {
                groupName = SchedulerConstants.DefaultGroup;
            }

            resources.JobStore.PauseJob(jobName, groupName);
            NotifySchedulerThread(null);
            NotifySchedulerListenersPausedJob(jobName, groupName);
        }

        /// <summary>
        /// Pause all of the <see cref="JobDetail" />s in the
        /// given group - by pausing all of their <see cref="Trigger" />s.
        /// </summary>
        public virtual void PauseJobGroup(string groupName)
        {
            ValidateState();

            if (groupName == null)
            {
                groupName = SchedulerConstants.DefaultGroup;
            }

            resources.JobStore.PauseJobGroup(groupName);
            NotifySchedulerThread(null);
            NotifySchedulerListenersPausedJob(null, groupName);
        }

        /// <summary>
        /// Resume (un-pause) the <see cref="Trigger" /> with the given
        /// name.
        /// <p>
        /// If the <see cref="Trigger" /> missed one or more fire-times, then the
        /// <see cref="Trigger" />'s misfire instruction will be applied.
        /// </p>
        /// </summary>
        public virtual void ResumeTrigger(string triggerName, string groupName)
        {
            ValidateState();

            if (groupName == null)
            {
                groupName = SchedulerConstants.DefaultGroup;
            }

            resources.JobStore.ResumeTrigger(triggerName, groupName);
            NotifySchedulerThread(null);
            NotifySchedulerListenersResumedTrigger(triggerName, groupName);
        }

        /// <summary>
        /// Resume (un-pause) all of the <see cref="Trigger" />s in the
        /// given group.
        /// <p>
        /// If any <see cref="Trigger" /> missed one or more fire-times, then the
        /// <see cref="Trigger" />'s misfire instruction will be applied.
        /// </p>
        /// </summary>
        public virtual void ResumeTriggerGroup(string groupName)
        {
            ValidateState();

            if (groupName == null)
            {
                groupName = SchedulerConstants.DefaultGroup;
            }

            resources.JobStore.ResumeTriggerGroup(groupName);
            NotifySchedulerThread(null);
            NotifySchedulerListenersResumedTrigger(null, groupName);
        }

        /// <summary>
        /// Gets the paused trigger groups.
        /// </summary>
        /// <returns></returns>
        public virtual Collection.ISet<string> GetPausedTriggerGroups()
        {
            return resources.JobStore.GetPausedTriggerGroups();
        }

        /// <summary>
        /// Resume (un-pause) the <see cref="JobDetail" /> with
        /// the given name.
        /// <p>
        /// If any of the <see cref="IJob" />'s<see cref="Trigger" /> s missed one
        /// or more fire-times, then the <see cref="Trigger" />'s misfire
        /// instruction will be applied.
        /// </p>
        /// </summary>
        public virtual void ResumeJob(string jobName, string groupName)
        {
            ValidateState();

            if (groupName == null)
            {
                groupName = SchedulerConstants.DefaultGroup;
            }

            resources.JobStore.ResumeJob(jobName, groupName);
            NotifySchedulerThread(null);
            NotifySchedulerListenersResumedJob(jobName, groupName);
        }

        /// <summary>
        /// Resume (un-pause) all of the <see cref="JobDetail" />s
        /// in the given group.
        /// <p>
        /// If any of the <see cref="IJob" /> s had <see cref="Trigger" /> s that
        /// missed one or more fire-times, then the <see cref="Trigger" />'s
        /// misfire instruction will be applied.
        /// </p>
        /// </summary>
        public virtual void ResumeJobGroup(string groupName)
        {
            ValidateState();

            if (groupName == null)
            {
                groupName = SchedulerConstants.DefaultGroup;
            }

            resources.JobStore.ResumeJobGroup(groupName);
            NotifySchedulerThread(null);
            NotifySchedulerListenersResumedJob(null, groupName);
        }

        /// <summary>
        /// Pause all triggers - equivalent of calling <see cref="PauseTriggerGroup(string)" />
        /// on every group.
        /// <p>
        /// When <see cref="ResumeAll" /> is called (to un-pause), trigger misfire
        /// instructions WILL be applied.
        /// </p>
        /// </summary>
        /// <seealso cref="ResumeAll()" />
        /// <seealso cref="PauseJob" />
        public virtual void PauseAll()
        {
            ValidateState();

            resources.JobStore.PauseAll();
            NotifySchedulerThread(null);
            NotifySchedulerListenersPausedTrigger(null, null);
        }

        /// <summary>
        /// Resume (un-pause) all triggers - equivalent of calling <see cref="ResumeTriggerGroup(string)" />
        /// on every group.
        /// <p>
        /// If any <see cref="Trigger" /> missed one or more fire-times, then the
        /// <see cref="Trigger" />'s misfire instruction will be applied.
        /// </p>
        /// </summary>
        /// <seealso cref="PauseAll()" />
        public virtual void ResumeAll()
        {
            ValidateState();

            resources.JobStore.ResumeAll();
            NotifySchedulerThread(null);
            NotifySchedulerListenersResumedTrigger(null, null);
        }

        /// <summary>
        /// Get the names of all known <see cref="IJob" /> groups.
        /// </summary>
        public virtual IList<string> GetJobGroupNames()
        {
            ValidateState();

            return resources.JobStore.GetJobGroupNames();
        }

        /// <summary>
        /// Get the names of all the <see cref="IJob" />s in the
        /// given group.
        /// </summary>
        public virtual IList<string> GetJobNames(string groupName)
        {
            ValidateState();

            if (groupName == null)
            {
                groupName = SchedulerConstants.DefaultGroup;
            }

            return resources.JobStore.GetJobNames(groupName);
        }

        /// <summary> 
        /// Get all <see cref="Trigger" /> s that are associated with the
        /// identified <see cref="JobDetail" />.
        /// </summary>
        public virtual IList<Trigger> GetTriggersOfJob(string jobName, string groupName)
        {
            ValidateState();

            if (groupName == null)
            {
                groupName = SchedulerConstants.DefaultGroup;
            }

            return resources.JobStore.GetTriggersForJob(jobName, groupName);
        }

        /// <summary>
        /// Get the names of all known <see cref="Trigger" />
        /// groups.
        /// </summary>
        public virtual IList<string> GetTriggerGroupNames()
        {
            ValidateState();
            return resources.JobStore.GetTriggerGroupNames();
        }

        /// <summary>
        /// Get the names of all the <see cref="Trigger" />s in
        /// the given group.
        /// </summary>
        public virtual IList<string> GetTriggerNames(string groupName)
        {
            ValidateState();

            if (groupName == null)
            {
                groupName = SchedulerConstants.DefaultGroup;
            }

            return resources.JobStore.GetTriggerNames(groupName);
        }

        /// <summary> 
        /// Get the <see cref="JobDetail" /> for the <see cref="IJob" />
        /// instance with the given name and group.
        /// </summary>
        public virtual JobDetail GetJobDetail(string jobName, string jobGroup)
        {
            ValidateState();

            if (jobGroup == null)
            {
                jobGroup = SchedulerConstants.DefaultGroup;
            }

            return resources.JobStore.RetrieveJob(jobName, jobGroup);
        }

        /// <summary>
        /// Get the <see cref="Trigger" /> instance with the given name and
        /// group.
        /// </summary>
        public virtual Trigger GetTrigger(string triggerName, string triggerGroup)
        {
            ValidateState();

            if (triggerGroup == null)
            {
                triggerGroup = SchedulerConstants.DefaultGroup;
            }

            return resources.JobStore.RetrieveTrigger(triggerName, triggerGroup);
        }

        /// <summary>
        /// Get the current state of the identified <see cref="Trigger" />.  
        /// </summary>
        /// <seealso cref="TriggerState.Normal" />
        /// <seealso cref="TriggerState.Paused" />
        /// <seealso cref="TriggerState.Complete" />
        /// <seealso cref="TriggerState.Error" />      
        public virtual TriggerState GetTriggerState(string triggerName, string triggerGroup)
        {
            ValidateState();

            if (triggerGroup == null)
            {
                triggerGroup = SchedulerConstants.DefaultGroup;
            }

            return resources.JobStore.GetTriggerState(triggerName, triggerGroup);
        }

        /// <summary>
        /// Add (register) the given <see cref="ICalendar" /> to the Scheduler.
        /// </summary>
        public virtual void AddCalendar(string calName, ICalendar calendar, bool replace,
                                        bool updateTriggers)
        {
            ValidateState();
            resources.JobStore.StoreCalendar(calName, calendar, replace, updateTriggers);
        }

        /// <summary>
        /// Delete the identified <see cref="ICalendar" /> from the Scheduler.
        /// </summary>
        /// <returns> true if the Calendar was found and deleted.</returns>
        public virtual bool DeleteCalendar(string calName)
        {
            ValidateState();
            return resources.JobStore.RemoveCalendar(calName);
        }

        /// <summary> 
        /// Get the <see cref="ICalendar" /> instance with the given name.
        /// </summary>
        public virtual ICalendar GetCalendar(string calName)
        {
            ValidateState();
            return resources.JobStore.RetrieveCalendar(calName);
        }

        /// <summary>
        /// Get the names of all registered <see cref="ICalendar" />s.
        /// </summary>
        public virtual IList<string> GetCalendarNames()
        {
            ValidateState();
            return resources.JobStore.GetCalendarNames();
        }

        /// <summary>
        /// Add the given <see cref="IJobListener" /> to the
        /// <see cref="IScheduler" />'s<i>global</i> list.
        /// </summary>
        public void AddGlobalJobListener(IJobListener jobListener)
        {
            if (String.IsNullOrEmpty(jobListener.Name))
            {
                throw new ArgumentException("JobListener name cannot be empty.");
            }
            lock (globalJobListeners)
            {
                globalJobListeners[jobListener.Name] = jobListener;
            }
        }

        /// <summary>
        /// Remove the identifed <see cref="IJobListener" /> from the <see cref="IScheduler" />'s
        /// list of <i>global</i> listeners. 
        /// </summary>
        /// <param name="name"></param>
        /// <returns>true if the identifed listener was found in the list, and removed.</returns>
        public bool RemoveGlobalJobListener(string name)
        {
            lock (globalJobListeners)
            {
                if (globalJobListeners.ContainsKey(name))
                {
                    globalJobListeners.Remove(name);
                    return true;
                }
                
                return false;
            }
        }

        /// <summary>
        /// Add the given <see cref="IJobListener" /> to the
        /// <see cref="IScheduler" />'s <i>internal</i> list.
        /// </summary>
        /// <param name="jobListener"></param>
        public void AddInternalJobListener(IJobListener jobListener)
        {
            if (jobListener.Name.IsNullOrWhiteSpace())
            {
                throw new ArgumentException("JobListener name cannot be empty.", "jobListener");
            }

            lock (internalJobListeners)
            {
                internalJobListeners[jobListener.Name] = jobListener;
            }
        }

        /// <summary>
        /// Remove the identified <see cref="IJobListener" /> from the <see cref="IScheduler" />'s
        /// list of <i>internal</i> listeners.
        /// </summary>
        /// <param name="name"></param>
        /// <returns>true if the identified listener was found in the list, and removed.</returns>
        public bool RemoveInternalJobListener(string name)
        {
            lock (internalJobListeners)
            {
                return internalJobListeners.Remove(name);
            }
        }

        /// <summary>
        /// Get a List containing all of the <code>{@link org.quartz.JobListener}</code>s
        /// in the <code>Scheduler</code>'s <i>internal</i> list.
        /// </summary>
        /// <returns></returns>
        public IList<IJobListener> InternalJobListeners
        {
            get
            {
                lock (internalJobListeners)
                {
                    return new List<IJobListener>(internalJobListeners.Values).AsReadOnly();
                }
            }
        }

        /// <summary>
        /// Get the <i>internal</i> <see cref="IJobListener" />
        /// that has the given name.
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public IJobListener GetInternalJobListener(string name)
        {
            lock (internalJobListeners)
            {
                IJobListener listener;
                internalJobListeners.TryGetValue(name, out listener);
                return listener;
            }
        }
    
        /// <summary>
        /// Add the given <see cref="ITriggerListener" /> to the
        /// <see cref="IScheduler" />'s <i>global</i> list.
        /// </summary>
        public virtual void AddGlobalTriggerListener(ITriggerListener triggerListener)
        {
            if (triggerListener.Name.IsNullOrWhiteSpace())
            {
                throw new ArgumentException("TriggerListener name cannot be empty.");
            }

            lock (globalTriggerListeners)
            {
                globalTriggerListeners[triggerListener.Name] = triggerListener;
            }
        }

        /// <summary>
        /// Remove the identified <see cref="ITriggerListener" /> from the <see cref="IScheduler" />'s
        /// list of <i>global</i> listeners.
        /// </summary>
        /// <param name="name"></param>
        /// <returns> true if the identified listener was found in the list, and removed</returns>
        public bool RemoveGlobalTriggerListener(string name)
        {
            lock (globalTriggerListeners)
            {
                return globalTriggerListeners.Remove(name);
            }
        }

        /// <summary>
        /// Get the <i>global</i> <see cref="ITriggerListener" /> that
        /// has the given name.
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public ITriggerListener GetGlobalTriggerListener(string name)
        {
            lock (globalTriggerListeners)
            {
                ITriggerListener triggerListener;
                globalTriggerListeners.TryGetValue(name, out triggerListener);
                return triggerListener;
            }
        }

        /// <summary>
        /// Add the given <code>{@link org.quartz.TriggerListener}</code> to the
        /// <code>Scheduler</code>'s <i>internal</i> list.
        /// </summary>
        /// <param name="triggerListener"></param>
        public void AddInternalTriggerListener(ITriggerListener triggerListener)
        {
            if (triggerListener.Name.IsNullOrWhiteSpace())
            {
                throw new ArgumentException("TriggerListener name cannot be empty.", "triggerListener");
            }

            lock (internalTriggerListeners)
            {
                internalTriggerListeners[triggerListener.Name] = triggerListener;
            }
        }

        /// <summary>
        /// Remove the identified <see cref="ITriggerListener" /> from the <see cref="IScheduler" />'s
        /// list of <i>internal</i> listeners.
        /// </summary>
        /// <param name="name"></param>
        /// <returns>true if the identified listener was found in the list, and removed.</returns>
        public bool RemoveinternalTriggerListener(string name)
        {
            lock (internalTriggerListeners)
            {
                return internalTriggerListeners.Remove(name);
            }
        }

        /// <summary>
        /// Get a list containing all of the <see cref="ITriggerListener" />s
        /// in the <see cref="IScheduler" />'s <i>internal</i> list.
        /// </summary>
        public IList<ITriggerListener> InternalTriggerListeners
        {
            get
            {
                lock (internalTriggerListeners)
                {
                    return new List<ITriggerListener>(internalTriggerListeners.Values).AsReadOnly();
                }
            }
        }

        /// <summary>
        /// Get the <i>internal</i> <code>{@link TriggerListener}</code> that
        /// has the given name.
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public ITriggerListener GetInternalTriggerListener(string name)
        {
            lock (internalTriggerListeners)
            {
                ITriggerListener triggerListener;
                internalTriggerListeners.TryGetValue(name, out triggerListener);
                return triggerListener;
            }
        }

        /// <summary>
        /// Register the given <see cref="ISchedulerListener" /> with the
        /// <see cref="IScheduler" />.
        /// </summary>
        public void AddSchedulerListener(ISchedulerListener schedulerListener)
        {
            lock (schedulerListeners)
            {
                schedulerListeners.Add(schedulerListener);
            }
        }

        /// <summary>
        /// Remove the given <see cref="ISchedulerListener" /> from the
        /// <see cref="IScheduler" />.
        /// </summary>
        /// <returns> 
        /// true if the identifed listener was found in the list, and removed.
        /// </returns>
        public virtual bool RemoveSchedulerListener(ISchedulerListener schedulerListener)
        {
            lock (schedulerListeners)
            {
                return schedulerListeners.Remove(schedulerListener);
            }
        }


        protected internal void NotifyJobStoreJobVetoed(Trigger trigger, JobDetail detail, SchedulerInstruction instCode)
        {

            resources.JobStore.TriggeredJobComplete(trigger, detail, instCode);
        }

        /// <summary>
        /// Notifies the job store job complete.
        /// </summary>
        /// <param name="trigger">The trigger.</param>
        /// <param name="detail">The detail.</param>
        /// <param name="instCode">The instruction code.</param>
        protected internal virtual void NotifyJobStoreJobComplete(Trigger trigger, JobDetail detail,
                                                                  SchedulerInstruction instCode)
        {
            resources.JobStore.TriggeredJobComplete(trigger, detail, instCode);
        }

        /// <summary>
        /// Notifies the scheduler thread.
        /// </summary>
        protected internal virtual void NotifySchedulerThread(DateTimeOffset? candidateNewNextFireTimeUtc)
        {
            if (SignalOnSchedulingChange)
            {
                schedThread.SignalSchedulingChange(candidateNewNextFireTimeUtc);
            }
        }

        private IEnumerable<ITriggerListener> BuildTriggerListenerList()
        {
            List<ITriggerListener> listeners = new List<ITriggerListener>();
            listeners.AddRange(GlobalTriggerListeners);
            listeners.AddRange(InternalTriggerListeners);
            return listeners;
        }

        private IEnumerable<IJobListener> BuildJobListenerList()
        {
            List<IJobListener> listeners = new List<IJobListener>();
            listeners.AddRange(GlobalJobListeners);
            listeners.AddRange(InternalJobListeners);
            return listeners;
        }


        private IList<ISchedulerListener> BuildSchedulerListenerList()
        {
            List<ISchedulerListener> allListeners = new List<ISchedulerListener>();
            allListeners.AddRange(SchedulerListeners);
            allListeners.AddRange(InternalSchedulerListeners);
            return allListeners;
        }

        /// <summary>
        /// Notifies the trigger listeners about fired trigger.
        /// </summary>
        /// <param name="jec">The job execution context.</param>
        /// <returns></returns>
        public virtual bool NotifyTriggerListenersFired(JobExecutionContext jec)
        {
            bool vetoedExecution = false;

            // build a list of all trigger listeners that are to be notified...
            IEnumerable<ITriggerListener> listeners = BuildTriggerListenerList();

            // notify all trigger listeners in the list
            foreach (ITriggerListener tl in listeners)
            {
                try
                {
                    tl.TriggerFired(jec.Trigger, jec);

                    if (tl.VetoJobExecution(jec.Trigger, jec))
                    {
                        vetoedExecution = true;
                    }
                }
                catch (Exception e)
                {
                    SchedulerException se = new SchedulerException(string.Format(CultureInfo.InvariantCulture, "TriggerListener '{0}' threw exception: {1}", tl.Name, e.Message), e);
                    throw se;
                }
            }

            return vetoedExecution;
        }


        /// <summary>
        /// Notifies the trigger listeners about misfired trigger.
        /// </summary>
        /// <param name="trigger">The trigger.</param>
        public virtual void NotifyTriggerListenersMisfired(Trigger trigger)
        {
            // build a list of all trigger listeners that are to be notified...
            IEnumerable<ITriggerListener> listeners = BuildTriggerListenerList();

            // notify all trigger listeners in the list
            foreach (ITriggerListener tl in listeners)
            {
                try
                {
                    tl.TriggerMisfired(trigger);
                }
                catch (Exception e)
                {
                    SchedulerException se = new SchedulerException(string.Format(CultureInfo.InvariantCulture, "TriggerListener '{0}' threw exception: {1}", tl.Name, e.Message), e);
                    throw se;
                }
            }
        }

        /// <summary>
        /// Notifies the trigger listeners of completion.
        /// </summary>
        /// <param name="jec">The job executution context.</param>
        /// <param name="instCode">The instruction code to report to triggers.</param>
        public virtual void NotifyTriggerListenersComplete(JobExecutionContext jec, SchedulerInstruction instCode)
        {
            // build a list of all trigger listeners that are to be notified...
            IEnumerable<ITriggerListener> listeners = BuildTriggerListenerList();

            // notify all trigger listeners in the list
            foreach (ITriggerListener tl in listeners)
            {
                try
                {
                    tl.TriggerComplete(jec.Trigger, jec, instCode);
                }
                catch (Exception e)
                {
                    SchedulerException se = new SchedulerException(string.Format(CultureInfo.InvariantCulture, "TriggerListener '{0}' threw exception: {1}", tl.Name, e.Message), e);
                    throw se;
                }
            }
        }

        /// <summary>
        /// Notifies the job listeners about job to be executed.
        /// </summary>
        /// <param name="jec">The jec.</param>
        public virtual void NotifyJobListenersToBeExecuted(JobExecutionContext jec)
        {
            // build a list of all job listeners that are to be notified...
            IEnumerable<IJobListener> listeners = BuildJobListenerList();

            // notify all job listeners
            foreach (IJobListener jl in listeners)
            {
                try
                {
                    jl.JobToBeExecuted(jec);
                }
                catch (Exception e)
                {
                    SchedulerException se = new SchedulerException(string.Format(CultureInfo.InvariantCulture, "JobListener '{0}' threw exception: {1}", jl.Name, e.Message), e);
                    throw se;
                }
            }
        }

        /// <summary>
        /// Notifies the job listeners that job exucution was vetoed.
        /// </summary>
        /// <param name="jec">The job execution context.</param>
        public virtual void NotifyJobListenersWasVetoed(JobExecutionContext jec)
        {
            // build a list of all job listeners that are to be notified...
            IEnumerable<IJobListener> listeners = BuildJobListenerList();

            // notify all job listeners
            foreach (IJobListener jl in listeners)
            {
                try
                {
                    jl.JobExecutionVetoed(jec);
                }
                catch (Exception e)
                {
                    SchedulerException se = new SchedulerException(string.Format(CultureInfo.InvariantCulture, "JobListener '{0}' threw exception: {1}", jl.Name, e.Message), e);
                    throw se;
                }
            }
        }

        /// <summary>
        /// Notifies the job listeners that job was executed.
        /// </summary>
        /// <param name="jec">The jec.</param>
        /// <param name="je">The je.</param>
        public virtual void NotifyJobListenersWasExecuted(JobExecutionContext jec, JobExecutionException je)
        {
            // build a list of all job listeners that are to be notified...
            IEnumerable<IJobListener> listeners = BuildJobListenerList();

            // notify all job listeners
            foreach (IJobListener jl in listeners)
            {
                try
                {
                    jl.JobWasExecuted(jec, je);
                }
                catch (Exception e)
                {
                    SchedulerException se = new SchedulerException(string.Format(CultureInfo.InvariantCulture, "JobListener '{0}' threw exception: {1}", jl.Name, e.Message), e);
                    throw se;
                }
            }
        }

        /// <summary>
        /// Notifies the scheduler listeners about scheduler error.
        /// </summary>
        /// <param name="msg">The MSG.</param>
        /// <param name="se">The se.</param>
        public virtual void NotifySchedulerListenersError(string msg, SchedulerException se)
        {
            // build a list of all scheduler listeners that are to be notified...
            IList<ISchedulerListener> schedListeners = BuildSchedulerListenerList();

            // notify all scheduler listeners
            foreach (ISchedulerListener sl in schedListeners)
            {
                try
                {
                    sl.SchedulerError(msg, se);
                }
                catch (Exception e)
                {
                    log.Error("Error while notifying SchedulerListener of error: ", e);
                    log.Error("  Original error (for notification) was: " + msg, se);
                }
            }
        }

        /// <summary>
        /// Notifies the scheduler listeners about job that was scheduled.
        /// </summary>
        /// <param name="trigger">The trigger.</param>
        public virtual void NotifySchedulerListenersScheduled(Trigger trigger)
        {
            // build a list of all scheduler listeners that are to be notified...
            IList<ISchedulerListener> schedListeners = BuildSchedulerListenerList();

            // notify all scheduler listeners
            foreach (ISchedulerListener sl in schedListeners)
            {
                try
                {
                    sl.JobScheduled(trigger);
                }
                catch (Exception e)
                {
                    log.Error(string.Format(CultureInfo.InvariantCulture, "Error while notifying SchedulerListener of scheduled job.  Triger={0}", trigger.FullName), e);
                }
            }
        }

        /// <summary>
        /// Notifies the scheduler listeners about job that was unscheduled.
        /// </summary>
        /// <param name="triggerName">Name of the trigger.</param>
        /// <param name="triggerGroup">The trigger group.</param>
        public virtual void NotifySchedulerListenersUnscheduled(string triggerName, string triggerGroup)
        {
            // build a list of all scheduler listeners that are to be notified...
            IList<ISchedulerListener> schedListeners = BuildSchedulerListenerList();

            // notify all scheduler listeners
            foreach (ISchedulerListener sl in schedListeners)
            {
                try
                {
                    sl.JobUnscheduled(triggerName, triggerGroup);
                }
                catch (Exception e)
                {
                    log.Error(
                        string.Format(CultureInfo.InvariantCulture, "Error while notifying SchedulerListener of unscheduled job.  Triger={0}.{1}", triggerGroup, triggerName), e);
                }
            }
        }

        /// <summary>
        /// Notifies the scheduler listeners about finalized trigger.
        /// </summary>
        /// <param name="trigger">The trigger.</param>
        public virtual void NotifySchedulerListenersFinalized(Trigger trigger)
        {
            // build a list of all scheduler listeners that are to be notified...
            IList<ISchedulerListener> schedListeners = BuildSchedulerListenerList();

            // notify all scheduler listeners
            foreach (ISchedulerListener sl in schedListeners)
            {
                try
                {
                    sl.TriggerFinalized(trigger);
                }
                catch (Exception e)
                {
                    log.Error(string.Format(CultureInfo.InvariantCulture, "Error while notifying SchedulerListener of finalized trigger.  Triger={0}", trigger.FullName), e);
                }
            }
        }

        /// <summary>
        /// Notifies the scheduler listeners about paused trigger.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <param name="group">The group.</param>
        public virtual void NotifySchedulerListenersPausedTrigger(string name, string group)
        {
            // build a list of all job listeners that are to be notified...
            IList<ISchedulerListener> schedListeners = BuildSchedulerListenerList();

            // notify all scheduler listeners
            foreach (ISchedulerListener sl in schedListeners)
            {
                try
                {
                    sl.TriggersPaused(name, group);
                }
                catch (Exception e)
                {
                    log.Error(string.Format(CultureInfo.InvariantCulture, "Error while notifying SchedulerListener of paused trigger/group.  Triger={0}.{1}", group, name), e);
                }
            }
        }

        /// <summary>
        /// Notifies the scheduler listeners resumed trigger.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <param name="group">The group.</param>
        public virtual void NotifySchedulerListenersResumedTrigger(string name, string group)
        {
            // build a list of all job listeners that are to be notified...
            IList<ISchedulerListener> schedListeners = BuildSchedulerListenerList();

            // notify all scheduler listeners
            foreach (ISchedulerListener sl in schedListeners)
            {
                try
                {
                    sl.TriggersResumed(name, group);
                }
                catch (Exception e)
                {
                    log.Error(string.Format(CultureInfo.InvariantCulture, "Error while notifying SchedulerListener of resumed trigger/group.  Triger={0}.{1}", group, name), e);
                }
            }
        }

        /// <summary>
        /// Notifies the scheduler listeners about paused job.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <param name="group">The group.</param>
        public virtual void NotifySchedulerListenersPausedJob(string name, string group)
        {
            // build a list of all job listeners that are to be notified...
            IList<ISchedulerListener> schedListeners = BuildSchedulerListenerList();

            // notify all scheduler listeners
            foreach (ISchedulerListener sl in schedListeners)
            {
                try
                {
                    sl.JobsPaused(name, group);
                }
                catch (Exception e)
                {
                    log.Error(string.Format(CultureInfo.InvariantCulture, "Error while notifying SchedulerListener of paused job/group.  Job={0}.{1}", group, name), e);
                }
            }
        }

        /// <summary>
        /// Notifies the scheduler listeners about resumed job.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <param name="group">The group.</param>
        public virtual void NotifySchedulerListenersResumedJob(string name, string group)
        {
            // build a list of all job listeners that are to be notified...
            IList<ISchedulerListener> schedListeners = BuildSchedulerListenerList();

            // notify all scheduler listeners
            foreach (ISchedulerListener sl in schedListeners)
            {
                try
                {
                    sl.JobsResumed(name, group);
                }
                catch (Exception e)
                {
                    log.Error(string.Format(CultureInfo.InvariantCulture, "Error while notifying SchedulerListener of resumed job/group.  Job={0}.{1}", group, name), e);
                }
            }
        }

        public void NotifySchedulerListenersInStandbyMode()
        {
            // notify all scheduler listeners
            foreach (ISchedulerListener listener in BuildSchedulerListenerList())
            {
                try
                {
                    listener.SchedulerInStandbyMode();
                }
                catch (Exception e)
                {
                    log.Error("Error while notifying SchedulerListener of inStandByMode.", e);
                }
            }
        }

        public void NotifySchedulerListenersStarted()
        {
            // notify all scheduler listeners
            foreach (ISchedulerListener listener in BuildSchedulerListenerList())
            {
                try
                {
                    listener.SchedulerStarted();
                }
                catch (Exception e)
                {
                    log.Error("Error while notifying SchedulerListener of startup.", e);
                }
            }
        }

        /// <summary>
        /// Notifies the scheduler listeners about scheduler shutdown.
        /// </summary>
        public virtual void NotifySchedulerListenersShutdown()
        {
            // build a list of all job listeners that are to be notified...
            IList<ISchedulerListener> schedListeners = BuildSchedulerListenerList();

            // notify all scheduler listeners
            foreach (ISchedulerListener sl in schedListeners)
            {
                try
                {
                    sl.SchedulerShutdown();
                }
                catch (Exception e)
                {
                    log.Error("Error while notifying SchedulerListener of Shutdown.", e);
                }
            }
        }


        public virtual void NotifySchedulerListenersShuttingdown()
        {
            // build a list of all job listeners that are to be notified...
            IList<ISchedulerListener> schedListeners = BuildSchedulerListenerList();

            // notify all scheduler listeners
            foreach (ISchedulerListener sl in schedListeners)
            {
                try
                {
                    sl.SchedulerShuttingdown();
                }
                catch (Exception e)
                {
                    log.Error("Error while notifying SchedulerListener of shutdown.", e);
                }
            }
        }
    

        public virtual void NotifySchedulerListenersJobAdded(JobDetail jobDetail)
        {
            // notify all scheduler listeners
            foreach (ISchedulerListener listener in BuildSchedulerListenerList())
            {
                try
                {
                    listener.JobAdded(jobDetail);
                }
                catch (Exception e)
                {
                    log.Error("Error while notifying SchedulerListener of JobAdded.", e);
                }
            }
        }

        public virtual void NotifySchedulerListenersJobDeleted(String jobName, String groupName)
        {
            // notify all scheduler listeners
            foreach (ISchedulerListener listener in BuildSchedulerListenerList())
            {
                try
                {
                    listener.JobDeleted(jobName, groupName);
                }
                catch (Exception e)
                {
                    log.Error("Error while notifying SchedulerListener of JobAdded.", e);
                }
            }
        }

        /// <summary>
        /// Interrupt all instances of the identified InterruptableJob.
        /// </summary>
        public virtual bool Interrupt(string jobName, string groupName)
        {
            if (groupName == null)
            {
                groupName = SchedulerConstants.DefaultGroup;
            }

            IList<JobExecutionContext> jobs = CurrentlyExecutingJobs;

            JobDetail jobDetail;

            bool interrupted = false;

            foreach (JobExecutionContext jec in jobs)
            {
                jobDetail = jec.JobDetail;
                if (jobName.Equals(jobDetail.Name) && groupName.Equals(jobDetail.Group))
                {
                    IJob job = jec.JobInstance;
                    if (job is IInterruptableJob)
                    {
                        ((IInterruptableJob)job).Interrupt();
                        interrupted = true;
                    }
                    else
                    {
                        throw new UnableToInterruptJobException(string.Format(CultureInfo.InvariantCulture, "Job '{0}' of group '{1}' can not be interrupted, since it does not implement {2}", jobName, groupName, typeof(IInterruptableJob).FullName));
                    }
                }
            }

            return interrupted;
        }

        private void ShutdownPlugins()
        {
            foreach (ISchedulerPlugin plugin in resources.SchedulerPlugins)
            {
                plugin.Shutdown();
            }
        }

        private void StartPlugins()
        {
            foreach (ISchedulerPlugin plugin in resources.SchedulerPlugins)
            {
                plugin.Start();
            }
        }

        public bool IsJobGroupPaused(string groupName)
        {
            return resources.JobStore.IsJobGroupPaused(groupName);
        }

        public bool IsTriggerGroupPaused(string groupName)
        {
            return resources.JobStore.IsTriggerGroupPaused(groupName);
        }

        ///<summary>
        ///Obtains a lifetime service object to control the lifetime policy for this instance.
        ///</summary>
        public override object InitializeLifetimeService()
        {
            // overriden to initialize null life time service,
            // this basically means that remoting object will live as long
            // as the application lives
            return null;
        }
    }

    /// <summary>
    /// ErrorLogger - Scheduler Listener Class
    /// </summary>
    internal class ErrorLogger : SchedulerListenerSupport
    {
        public override void SchedulerError(string msg, SchedulerException cause)
        {
            Log.Error(msg, cause);
        }
    }

    /////////////////////////////////////////////////////////////////////////////
    //
    // ExecutingJobsManager - Job Listener Class
    //
    /////////////////////////////////////////////////////////////////////////////
    internal class ExecutingJobsManager : IJobListener
    {
        public virtual string Name
        {
            get { return GetType().FullName; }
        }

        public virtual int NumJobsCurrentlyExecuting
        {
            get
            {
                lock (executingJobs)
                {
                    return executingJobs.Count;
                }
            }
        }

        public virtual int NumJobsFired
        {
            get { return numJobsFired; }
        }

        public virtual IList<JobExecutionContext> ExecutingJobs
        {
            get
            {
                lock (executingJobs)
                {
                    return new List<JobExecutionContext>(executingJobs.Values).AsReadOnly();
                }
            }
        }

        internal Dictionary<string, JobExecutionContext> executingJobs = new Dictionary<string, JobExecutionContext>();

        internal int numJobsFired;

        public virtual void JobToBeExecuted(JobExecutionContext context)
        {
            numJobsFired++;

            lock (executingJobs)
            {
                executingJobs[context.Trigger.FireInstanceId] = context;
            }
        }

        public virtual void JobWasExecuted(JobExecutionContext context, JobExecutionException jobException)
        {
            lock (executingJobs)
            {
                executingJobs.Remove(context.Trigger.FireInstanceId);
            }
        }

        public virtual void JobExecutionVetoed(JobExecutionContext context)
        {
        }
    }
}
