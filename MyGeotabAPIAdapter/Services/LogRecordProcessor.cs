using Geotab.Checkmate.ObjectModel;
using Microsoft.Extensions.Hosting;
using MyGeotabAPIAdapter.Add_Ons.VSS;
using MyGeotabAPIAdapter.Configuration;
using MyGeotabAPIAdapter.Configuration.Add_Ons.VSS;
using MyGeotabAPIAdapter.Database;
using MyGeotabAPIAdapter.Database.DataAccess;
using MyGeotabAPIAdapter.Database.EntityPersisters;
using MyGeotabAPIAdapter.Database.Models;
using MyGeotabAPIAdapter.Database.Models.Add_Ons.VSS;
using MyGeotabAPIAdapter.Exceptions;
using MyGeotabAPIAdapter.GeotabObjectMappers;
using MyGeotabAPIAdapter.Logging;
using MyGeotabAPIAdapter.MyGeotabAPI;
using NLog;
using Polly;
using System.Linq;
using Polly.Retry;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MyGeotabAPIAdapter.Services
{
    /// <summary>
    /// A <see cref="BackgroundService"/> that extracts <see cref="LogRecord"/> objects from a MyGeotab database and inserts/updates corresponding records in the Adapter database. 
    /// </summary>
    class LogRecordProcessor : BackgroundService
    {
        private readonly IAdlsService _adlsService;
        private readonly IGenericDatabaseUnitOfWorkContext<AdapterDatabaseUnitOfWorkContext> adapterContext;
        bool feedVersionRollbackRequired = false;

        string CurrentClassName { get => $"{GetType().Assembly.GetName().Name}.{GetType().Name} (v{GetType().Assembly.GetName().Version})"; }
        string DefaultErrorMessagePrefix { get => $"{CurrentClassName} process caught an exception"; }

        // Polly-related items:
        const int MaxRetries = 10;
        readonly AsyncRetryPolicy asyncRetryPolicyForDatabaseTransactions;

        readonly IAdapterConfiguration adapterConfiguration;
        readonly IAdapterEnvironment adapterEnvironment;
        readonly IExceptionHelper exceptionHelper;
        readonly IGenericEntityPersister<DbOVDSServerCommand> dbOVDSServerCommandEntityPersister;
        readonly IGenericEntityPersister<DbLogRecord> dbLogRecordEntityPersister;
        readonly IGenericGeotabObjectFeeder<LogRecord> logRecordGeotabObjectFeeder;
        readonly IGeotabDeviceFilterer geotabDeviceFilterer;
        readonly IGeotabLogRecordDbLogRecordObjectMapper geotabLogRecordDbLogRecordObjectMapper;
        readonly IMinimumIntervalSampler<LogRecord> minimumIntervalSampler;
        readonly IMyGeotabAPIHelper myGeotabAPIHelper;
        readonly IPrerequisiteServiceChecker prerequisiteServiceChecker;
        readonly IServiceTracker serviceTracker;
        readonly IStateMachine stateMachine;
        readonly IVSSConfiguration vssConfiguration;
        readonly IVSSObjectMapper vssObjectMapper;

        readonly Logger logger = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Initializes a new instance of the <see cref="LogRecordProcessor"/> class.
        /// </summary>
        public LogRecordProcessor(
            IAdapterConfiguration adapterConfiguration, 
            IAdapterEnvironment adapterEnvironment, 
            IExceptionHelper exceptionHelper, 
            IAdlsService adlsService,
            IGenericDatabaseUnitOfWorkContext<AdapterDatabaseUnitOfWorkContext> adapterContext,
            IGenericEntityPersister<DbOVDSServerCommand> dbOVDSServerCommandEntityPersister, 
            IGenericEntityPersister<DbLogRecord> dbLogRecordEntityPersister, 
            IGeotabDeviceFilterer geotabDeviceFilterer, 
            IGenericGeotabObjectFeeder<LogRecord> logRecordGeotabObjectFeeder, 
            IGeotabLogRecordDbLogRecordObjectMapper geotabLogRecordDbLogRecordObjectMapper, 
            IMinimumIntervalSampler<LogRecord> minimumIntervalSampler, 
            IMyGeotabAPIHelper myGeotabAPIHelper, 
            IPrerequisiteServiceChecker prerequisiteServiceChecker, 
            IServiceTracker serviceTracker, 
            IStateMachine stateMachine, 
            IVSSConfiguration vssConfiguration, 
            IVSSObjectMapper vssObjectMapper)
        {
            MethodBase methodBase = MethodBase.GetCurrentMethod();
            logger.Trace($"Begin {methodBase.ReflectedType.Name}.{methodBase.Name}");

            this.adapterConfiguration = adapterConfiguration;
            this.adapterEnvironment = adapterEnvironment;
            this.exceptionHelper = exceptionHelper;
            this._adlsService = adlsService;
            this.adapterContext = adapterContext;
            this.dbOVDSServerCommandEntityPersister = dbOVDSServerCommandEntityPersister;
            this.dbLogRecordEntityPersister = dbLogRecordEntityPersister;
            this.geotabDeviceFilterer = geotabDeviceFilterer;
            this.logRecordGeotabObjectFeeder = logRecordGeotabObjectFeeder;
            this.geotabLogRecordDbLogRecordObjectMapper = geotabLogRecordDbLogRecordObjectMapper;
            this.minimumIntervalSampler = minimumIntervalSampler;
            this.myGeotabAPIHelper = myGeotabAPIHelper;
            this.prerequisiteServiceChecker = prerequisiteServiceChecker;
            this.serviceTracker = serviceTracker;
            this.stateMachine = stateMachine;
            this.vssConfiguration = vssConfiguration;
            this.vssObjectMapper = vssObjectMapper;

            // Setup a database transaction retry policy.
            asyncRetryPolicyForDatabaseTransactions = DatabaseResilienceHelper.CreateAsyncRetryPolicyForDatabaseTransactions<Exception>(logger);

            logger.Trace($"End {methodBase.ReflectedType.Name}.{methodBase.Name}");
        }

        /// <summary>
        /// Iteratively executes the business logic until the service is stopped.
        /// </summary>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            MethodBase methodBase = MethodBase.GetCurrentMethod();
            logger.Trace($"Begin {methodBase.ReflectedType.Name}.{methodBase.Name}");

            while (!stoppingToken.IsCancellationRequested)
            {
                await WaitForPrerequisiteServicesIfNeededAsync(stoppingToken);

                if (stateMachine.CurrentState == State.Waiting)
                {
                    feedVersionRollbackRequired = true;
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    continue;
                }

                try
                {
                    logger.Trace($"Started iteration of {methodBase.ReflectedType.Name}.{methodBase.Name}");

                    using (var cancellationTokenSource = new CancellationTokenSource())
                    {
                        var dbOServiceTracking = await serviceTracker.GetLogRecordServiceInfoAsync();

                        if (!logRecordGeotabObjectFeeder.IsInitialized)
                        {
                            await logRecordGeotabObjectFeeder.InitializeAsync(
                                cancellationTokenSource,
                                adapterConfiguration.LogRecordFeedIntervalSeconds,
                                myGeotabAPIHelper.GetFeedResultLimitDefault,
                                (long?)dbOServiceTracking.LastProcessedFeedVersion);
                        }

                        if (feedVersionRollbackRequired)
                        {
                            logRecordGeotabObjectFeeder.LastFeedVersion = dbOServiceTracking.LastProcessedFeedVersion;
                            logRecordGeotabObjectFeeder.LastFeedRetrievalTimeUtc = DateTime.MinValue;
                            feedVersionRollbackRequired = false;
                        }

                        await logRecordGeotabObjectFeeder.GetFeedDataBatchAsync(cancellationTokenSource);
                        stoppingToken.ThrowIfCancellationRequested();

                        var logRecords = logRecordGeotabObjectFeeder.GetFeedResultDataValuesList();
                        if (logRecords.Count > 0)
                        {
                            var filteredLogRecords = await geotabDeviceFilterer.ApplyDeviceFilterAsync(cancellationTokenSource, logRecords);
                            filteredLogRecords = await minimumIntervalSampler.ApplyMinimumIntervalAsync(cancellationTokenSource, filteredLogRecords);
                            
                            // Wrap all operations in UnitOfWork
                            await asyncRetryPolicyForDatabaseTransactions.ExecuteAsync(async pollyContext =>
                            {
                                using (var adapterUOW = adapterContext.CreateUnitOfWork(Databases.AdapterDatabase))
                                {
                                    try
                                    {
                                        // Map the records to match the database structure
                                        var datalakeRecords = filteredLogRecords.Select(lr => new DbLogRecord
                                        {
                                            GeotabId = adapterConfiguration.MyGeotabDatabase,
                                            DateTime = lr.DateTime != null ? lr.DateTime.Value : DateTime.MinValue,
                                            DeviceId = lr.Device?.Id.ToString(),
                                            Latitude = Convert.ToDouble(lr.Latitude),
                                            Longitude = Convert.ToDouble(lr.Longitude),
                                            Speed = Convert.ToSingle(lr.Speed),
                                            RecordCreationTimeUtc = DateTime.UtcNow
                                        }).ToList();

                                        // Write to ADLS with the database-structured records
                                        var path = $"logrecords/{DateTime.UtcNow:yyyy/MM/dd/HH}/{Guid.NewGuid()}.json";
                                        await _adlsService.WriteDataAsync(path, datalakeRecords);

                                        // Persist to database
                                        var dbLogRecordsToPersist = geotabLogRecordDbLogRecordObjectMapper.CreateEntities(filteredLogRecords);
                                        await dbLogRecordEntityPersister.PersistEntitiesToDatabaseAsync(adapterContext, dbLogRecordsToPersist, cancellationTokenSource, Logging.LogLevel.Info);
                                        
                                        // Update tracking
                                        await serviceTracker.UpdateDbOServiceTrackingRecordAsync(
                                            adapterContext, 
                                            AdapterService.LogRecordProcessor, 
                                            logRecordGeotabObjectFeeder.LastFeedRetrievalTimeUtc, 
                                            logRecordGeotabObjectFeeder.LastFeedVersion);

                                        await adapterUOW.CommitAsync();
                                    }
                                    catch (Exception ex)
                                    {
                                        await adapterUOW.RollBackAsync();
                                        throw;
                                    }
                                }
                            }, new Context());
                        }
                        else
                        {
                            // Wrap database operations in UnitOfWork
                            await asyncRetryPolicyForDatabaseTransactions.ExecuteAsync(async pollyContext =>
                            {
                                using (var adapterUOW = adapterContext.CreateUnitOfWork(Databases.AdapterDatabase))
                                {
                                    try
                                    {
                                        await serviceTracker.UpdateDbOServiceTrackingRecordAsync(
                                            adapterContext,
                                            AdapterService.LogRecordProcessor,
                                            DateTime.UtcNow);
                                        await adapterUOW.CommitAsync();
                                    }
                                    catch (Exception ex)
                                    {
                                        await adapterUOW.RollBackAsync();
                                        throw;
                                    }
                                }
                            }, new Context());
                        }

                        logRecordGeotabObjectFeeder.FeedResultData.Clear();
                    }

                    logger.Trace($"Completed iteration of {methodBase.ReflectedType.Name}.{methodBase.Name}");
                }
                catch (OperationCanceledException)
                {
                    string errorMessage = $"{CurrentClassName} process cancelled.";
                    logger.Warn(errorMessage);
                    throw new Exception(errorMessage);
                }
                catch (Azure.RequestFailedException adlsException)
                {
                    HandleException(adlsException, NLogLogLevelName.Error, DefaultErrorMessagePrefix);
                }
                catch (MyGeotabConnectionException myGeotabConnectionException)
                {
                    HandleException(myGeotabConnectionException, NLogLogLevelName.Error, DefaultErrorMessagePrefix);
                }
                catch (Exception ex)
                {
                    HandleException(ex, NLogLogLevelName.Fatal, DefaultErrorMessagePrefix);
                }

                if (logRecordGeotabObjectFeeder.FeedCurrent)
                {
                    var delayTimeSpan = TimeSpan.FromSeconds(adapterConfiguration.LogRecordFeedIntervalSeconds);
                    logger.Info($"{CurrentClassName} pausing for the configured feed interval ({delayTimeSpan}).");
                    await Task.Delay(delayTimeSpan, stoppingToken);
                }
            }

            logger.Trace($"End {methodBase.ReflectedType.Name}.{methodBase.Name}");
        }

        /// <summary>
        /// Handles exceptions by logging them and updating the state machine if necessary.
        /// </summary>
        void HandleException(Exception exception, NLogLogLevelName logLevel, string errorMessagePrefix)
        {
            exceptionHelper.LogException(exception, logLevel, errorMessagePrefix);
            
            if (exception is Azure.RequestFailedException)
            {
                stateMachine.SetState(State.Waiting, StateReason.AdapterDatabaseNotAvailable);
            }
            else if (exception is MyGeotabConnectionException)
            {
                stateMachine.SetState(State.Waiting, StateReason.MyGeotabNotAvailable);
            }

            if (logLevel == NLogLogLevelName.Fatal)
            {
                System.Diagnostics.Process.GetCurrentProcess().Kill();
            }
        }

        /// <summary>
        /// Starts the processor if enabled in configuration.
        /// </summary>
        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            MethodBase methodBase = MethodBase.GetCurrentMethod();
            logger.Trace($"Begin {methodBase.ReflectedType.Name}.{methodBase.Name}");

            var dbOserviceTrackings = await serviceTracker.GetDbOServiceTrackingListAsync();
            adapterEnvironment.ValidateAdapterEnvironment(dbOserviceTrackings, AdapterService.LogRecordProcessor, adapterConfiguration.DisableMachineNameValidation);

            await asyncRetryPolicyForDatabaseTransactions.ExecuteAsync(async pollyContext =>
            {
                using (var adapterUOW = adapterContext.CreateUnitOfWork(Databases.AdapterDatabase))
                {
                    try
                    {
                        await serviceTracker.UpdateDbOServiceTrackingRecordAsync(
                            adapterContext,
                            AdapterService.LogRecordProcessor,
                            adapterEnvironment.AdapterVersion.ToString(),
                            adapterEnvironment.AdapterMachineName);
                        await adapterUOW.CommitAsync();
                    }
                    catch (Exception ex)
                    {
                        exceptionHelper.LogException(ex, NLogLogLevelName.Error, DefaultErrorMessagePrefix);
                        await adapterUOW.RollBackAsync();
                        throw;
                    }
                }
            }, new Context());

            if (adapterConfiguration.EnableLogRecordFeed == true)
            {
                logger.Info($"******** STARTING SERVICE: {CurrentClassName}");
                await base.StartAsync(cancellationToken);
            }
            else
            {
                logger.Warn($"******** WARNING - SERVICE DISABLED: The {CurrentClassName} service has not been enabled and will NOT be started.");
            }
        }

        /// <summary>
        /// Stops the processor.
        /// </summary>
        public override Task StopAsync(CancellationToken cancellationToken)
        {
            MethodBase methodBase = MethodBase.GetCurrentMethod();
            logger.Trace($"Begin {methodBase.ReflectedType.Name}.{methodBase.Name}");

            logger.Info($"******** STOPPED SERVICE: {CurrentClassName} ********");
            return base.StopAsync(cancellationToken);
        }

        /// <summary>
        /// Checks and waits for prerequisite services if needed.
        /// </summary>
        async Task WaitForPrerequisiteServicesIfNeededAsync(CancellationToken cancellationToken)
        {
            MethodBase methodBase = MethodBase.GetCurrentMethod();
            logger.Trace($"Begin {methodBase.ReflectedType.Name}.{methodBase.Name}");

            var prerequisiteServices = new List<AdapterService>
            {
                AdapterService.DeviceProcessor
            };

            await prerequisiteServiceChecker.WaitForPrerequisiteServicesIfNeededAsync(
                CurrentClassName,
                prerequisiteServices,
                cancellationToken);

            if (vssConfiguration.EnableVSSAddOn == true && 
                vssConfiguration.OutputLogRecordsToOVDS == true && 
                vssConfiguration.IsInitialized == false)
            {
                await vssConfiguration.InitializeAsync(
                    AppContext.BaseDirectory,
                    vssConfiguration.VSSPathMapFileName);
            }

            logger.Trace($"End {methodBase.ReflectedType.Name}.{methodBase.Name}");
        }
    }
}
