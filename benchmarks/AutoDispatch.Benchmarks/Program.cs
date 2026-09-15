using System.Collections.Generic;
using AutoDispatch;
using AutoDispatch.Benchmarks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

BenchmarkRunner.Run<DispatchBenchmarks>();

namespace AutoDispatch.Benchmarks
{
    // ---- AutoDispatch side ----
    public sealed record PingCommand(int Value);

    [Handler]
    public sealed class PingHandler
    {
        public Task<int> HandleAsync(PingCommand command, CancellationToken ct = default) =>
            Task.FromResult(command.Value + 1);
    }

    // ---- MediatR side ----
    public sealed record MediatRPingCommand(int Value) : IRequest<int>;

    public sealed class MediatRPingHandler : IRequestHandler<MediatRPingCommand, int>
    {
        public Task<int> Handle(MediatRPingCommand request, CancellationToken cancellationToken) =>
            Task.FromResult(request.Value + 1);
    }

    // ---- AutoDispatch notification side ----
    public sealed record PingNotification(int Value);

    [NotificationHandler]
    public sealed class PingNotificationHandlerA
    {
        public Task HandleAsync(PingNotification notification, CancellationToken ct = default) => Task.CompletedTask;
    }

    [NotificationHandler]
    public sealed class PingNotificationHandlerB
    {
        public Task HandleAsync(PingNotification notification, CancellationToken ct = default) => Task.CompletedTask;
    }

    // ---- MediatR notification side ----
    public sealed record MediatRPingNotification(int Value) : INotification;

    public sealed class MediatRPingNotificationHandlerA : INotificationHandler<MediatRPingNotification>
    {
        public Task Handle(MediatRPingNotification notification, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public sealed class MediatRPingNotificationHandlerB : INotificationHandler<MediatRPingNotification>
    {
        public Task Handle(MediatRPingNotification notification, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    // ---- AutoDispatch streaming side ----
    public sealed record PingStreamQuery(int Count);

    [StreamHandler]
    public sealed class PingStreamHandler
    {
        public async IAsyncEnumerable<int> HandleAsync(PingStreamQuery query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            for (var i = 0; i < query.Count; i++)
            {
                yield return i;
            }

            await Task.CompletedTask;
        }
    }

    // ---- MediatR streaming side ----
    public sealed record MediatRPingStreamQuery(int Count) : IStreamRequest<int>;

    public sealed class MediatRPingStreamHandler : IStreamRequestHandler<MediatRPingStreamQuery, int>
    {
        public async IAsyncEnumerable<int> Handle(MediatRPingStreamQuery request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            for (var i = 0; i < request.Count; i++)
            {
                yield return i;
            }

            await Task.CompletedTask;
        }
    }

    /// <summary>
    /// Compares per-call dispatch overhead (time + allocations) between AutoDispatch's
    /// compile-time generated <see cref="IDispatcher"/> and MediatR's reflection-based
    /// <see cref="IMediator"/>, both resolving a trivial handler that adds one to an int.
    /// A second benchmark pair compares <c>PublishAsync</c>/<c>Publish</c> fanning a
    /// notification out to two no-op handlers. A third pair compares <c>StreamAsync</c>/
    /// <c>CreateStream</c> fully enumerating a 10-item stream.
    /// </summary>
    [MemoryDiagnoser]
    public class DispatchBenchmarks
    {
        private IDispatcher _autoDispatch = null!;
        private IMediator _mediatr = null!;

        [GlobalSetup]
        public void Setup()
        {
            var autoDispatchServices = new ServiceCollection();
            autoDispatchServices.AddAutoDispatch();
            _autoDispatch = autoDispatchServices.BuildServiceProvider().GetRequiredService<IDispatcher>();

            var mediatrServices = new ServiceCollection();
            mediatrServices.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<MediatRPingHandler>());
            _mediatr = mediatrServices.BuildServiceProvider().GetRequiredService<IMediator>();
        }

        [Benchmark(Baseline = true)]
        public Task<int> AutoDispatch_SendAsync() => _autoDispatch.SendAsync(new PingCommand(41));

        [Benchmark]
        public Task<int> MediatR_Send() => _mediatr.Send(new MediatRPingCommand(41));

        [Benchmark]
        public Task AutoDispatch_PublishAsync() => _autoDispatch.PublishAsync(new PingNotification(41));

        [Benchmark]
        public Task MediatR_Publish() => _mediatr.Publish(new MediatRPingNotification(41));

        [Benchmark]
        public async Task AutoDispatch_StreamAsync()
        {
            await foreach (var _ in _autoDispatch.StreamAsync(new PingStreamQuery(10)))
            {
            }
        }

        [Benchmark]
        public async Task MediatR_CreateStream()
        {
            await foreach (var _ in _mediatr.CreateStream(new MediatRPingStreamQuery(10)))
            {
            }
        }
    }
}
