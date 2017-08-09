// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Infrastructure;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Abstractions.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Microsoft.AspNetCore.Server.Kestrel.Core
{
    public class ServerBuilder
    {

    }


    public class Server
    {
        private readonly List<ITransport> _transports = new List<ITransport>();
        // private readonly Heartbeat _heartbeat;
        private readonly ITransportFactory _transportFactory;

        private bool _hasStarted;
        private int _stopping;
        private readonly TaskCompletionSource<object> _stoppedTcs = new TaskCompletionSource<object>();

        public Server(IOptions<ServerOptions> options, ITransportFactory transportFactory, ILoggerFactory loggerFactory)
        {
            Options = options.Value;
            _transportFactory = transportFactory;
            Trace = loggerFactory.CreateLogger<Server>();
        }

        public static Server Create(Action<ServerOptions> callback)
        {
            return Create(null, callback);
        }

        public static Server Create(ITransportFactory transportFactory, Action<ServerOptions> callback)
        {
            return Create(transportFactory, NullLoggerFactory.Instance, callback);
        }

        public static Server Create(ITransportFactory transportFactory, ILoggerFactory loggerFactory, Action<ServerOptions> callback)
        {
            var options = new ServerOptions();
            callback(options);
            return new Server(Microsoft.Extensions.Options.Options.Create(options), transportFactory, loggerFactory);
        }

        //// For testing
        //internal KestrelServer(ITransportFactory transportFactory, ServiceContext serviceContext)
        //{
        //    if (transportFactory == null)
        //    {
        //        throw new ArgumentNullException(nameof(transportFactory));
        //    }

        //    _transportFactory = transportFactory;
        //    ServiceContext = serviceContext;

        //    var frameHeartbeatManager = new FrameHeartbeatManager(serviceContext.ConnectionManager);
        //    _heartbeat = new Heartbeat(
        //        new IHeartbeatHandler[] { serviceContext.DateHeaderValueManager, frameHeartbeatManager },
        //        serviceContext.SystemClock, Trace);

        //    Features = new FeatureCollection();
        //    _serverAddresses = new ServerAddressesFeature();
        //    Features.Set(_serverAddresses);
        //}

        //private static ServiceContext CreateServiceContext(IOptions<KestrelServerOptions> options, ILoggerFactory loggerFactory)
        //{
        //    if (options == null)
        //    {
        //        throw new ArgumentNullException(nameof(options));
        //    }
        //    if (loggerFactory == null)
        //    {
        //        throw new ArgumentNullException(nameof(loggerFactory));
        //    }

        //    var serverOptions = options.Value ?? new KestrelServerOptions();
        //    var logger = loggerFactory.CreateLogger("Microsoft.AspNetCore.Server.Kestrel");
        //    var trace = new KestrelTrace(logger);
        //    var connectionManager = new FrameConnectionManager(
        //        trace,
        //        serverOptions.Limits.MaxConcurrentConnections,
        //        serverOptions.Limits.MaxConcurrentUpgradedConnections);

        //    var systemClock = new SystemClock();
        //    var dateHeaderValueManager = new DateHeaderValueManager(systemClock);

        //    // TODO: This logic will eventually move into the IConnectionHandler<T> and off
        //    // the service context once we get to https://github.com/aspnet/KestrelHttpServer/issues/1662
        //    IThreadPool threadPool = null;
        //    switch (serverOptions.ApplicationSchedulingMode)
        //    {
        //        case SchedulingMode.Default:
        //        case SchedulingMode.ThreadPool:
        //            threadPool = new LoggingThreadPool(trace);
        //            break;
        //        case SchedulingMode.Inline:
        //            threadPool = new InlineLoggingThreadPool(trace);
        //            break;
        //        default:
        //            throw new NotSupportedException(CoreStrings.FormatUnknownTransportMode(serverOptions.ApplicationSchedulingMode));
        //    }

        //    return new ServiceContext
        //    {
        //        Log = trace,
        //        HttpParserFactory = frameParser => new HttpParser<FrameAdapter>(frameParser.Frame.ServiceContext.Log.IsEnabled(LogLevel.Information)),
        //        ThreadPool = threadPool,
        //        SystemClock = systemClock,
        //        DateHeaderValueManager = dateHeaderValueManager,
        //        ConnectionManager = connectionManager,
        //        ServerOptions = serverOptions
        //    };
        //}

        public ServerOptions Options { get; }

        // private ServiceContext ServiceContext { get; }

        private ILogger Trace { get; }

        // private FrameConnectionManager ConnectionManager => ServiceContext.ConnectionManager;

        public async Task StartAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                if (!BitConverter.IsLittleEndian)
                {
                    throw new PlatformNotSupportedException(CoreStrings.BigEndianNotSupported);
                }

                // ValidateOptions();

                if (_hasStarted)
                {
                    // The server has already started and/or has not been cleaned up yet
                    throw new InvalidOperationException(CoreStrings.ServerAlreadyStarted);
                }
                _hasStarted = true;
                // _heartbeat.Start();

                async Task OnBind(ListenOptions endpoint)
                {
                    var connectionHandler = new ConnectionHandler(endpoint);
                    var transport = _transportFactory.Create(endpoint, connectionHandler);
                    _transports.Add(transport);

                    await transport.BindAsync().ConfigureAwait(false);
                }

                await AddressBinder.BindAsync(new List<string>(), preferHostingUrls: false, listenOptions: Options.ListenOptions, logger: Trace, createBinding: OnBind).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Trace.LogCritical(0, ex, "Unable to start Kestrel.");
                Dispose();
                throw;
            }
        }

        // Graceful shutdown if possible
        public async Task StopAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            if (Interlocked.Exchange(ref _stopping, 1) == 1)
            {
                await _stoppedTcs.Task.ConfigureAwait(false);
                return;
            }

            try
            {
                var tasks = new Task[_transports.Count];
                for (int i = 0; i < _transports.Count; i++)
                {
                    tasks[i] = _transports[i].UnbindAsync();
                }
                await Task.WhenAll(tasks).ConfigureAwait(false);

                //if (!await ConnectionManager.CloseAllConnectionsAsync(cancellationToken).ConfigureAwait(false))
                //{
                //    Trace.NotAllConnectionsClosedGracefully();

                //    if (!await ConnectionManager.AbortAllConnectionsAsync().ConfigureAwait(false))
                //    {
                //        Trace.NotAllConnectionsAborted();
                //    }
                //}

                for (int i = 0; i < _transports.Count; i++)
                {
                    tasks[i] = _transports[i].StopAsync();
                }
                await Task.WhenAll(tasks).ConfigureAwait(false);

                // _heartbeat.Dispose();
            }
            catch (Exception ex)
            {
                _stoppedTcs.TrySetException(ex);
                throw;
            }

            _stoppedTcs.TrySetResult(null);
        }

        // Ungraceful shutdown
        public void Dispose()
        {
            var cancelledTokenSource = new CancellationTokenSource();
            cancelledTokenSource.Cancel();
            StopAsync(cancelledTokenSource.Token).GetAwaiter().GetResult();
        }

        //private void ValidateOptions()
        //{
        //    if (Options.Limits.MaxRequestBufferSize.HasValue &&
        //        Options.Limits.MaxRequestBufferSize < Options.Limits.MaxRequestLineSize)
        //    {
        //        throw new InvalidOperationException(
        //            CoreStrings.FormatMaxRequestBufferSmallerThanRequestLineBuffer(Options.Limits.MaxRequestBufferSize.Value, Options.Limits.MaxRequestLineSize));
        //    }

        //    if (Options.Limits.MaxRequestBufferSize.HasValue &&
        //        Options.Limits.MaxRequestBufferSize < Options.Limits.MaxRequestHeadersTotalSize)
        //    {
        //        throw new InvalidOperationException(
        //            CoreStrings.FormatMaxRequestBufferSmallerThanRequestHeaderBuffer(Options.Limits.MaxRequestBufferSize.Value, Options.Limits.MaxRequestHeadersTotalSize));
        //    }
        //}
    }
}