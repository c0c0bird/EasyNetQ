using EasyNetQ.Internals;

namespace EasyNetQ;

/// <summary>
///     Various generic extensions for <see cref="IRpc"/>
/// </summary>
public static class RpcExtensions
{
    /// <summary>
    ///     Makes an RPC style request
    /// </summary>
    /// <typeparam name="TRequest">The request type</typeparam>
    /// <typeparam name="TResponse">The response type</typeparam>
    /// <param name="rpc">The rpc instance.</param>
    /// <param name="request">The request message.</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <returns>The response</returns>
    public static TResponse Request<TRequest, TResponse>(
        this IRpc rpc,
        TRequest request,
        CancellationToken cancellationToken = default
    )
    {
        return rpc.RequestAsync<TRequest, TResponse>(request, cancellationToken)
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>
    ///     Makes an RPC style request
    /// </summary>
    /// <typeparam name="TRequest">The request type</typeparam>
    /// <typeparam name="TResponse">The response type</typeparam>
    /// <param name="rpc">The rpc instance.</param>
    /// <param name="request">The request message.</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <returns>The response</returns>
    public static Task<TResponse> RequestAsync<TRequest, TResponse>(
        this IRpc rpc,
        TRequest request,
        CancellationToken cancellationToken = default
    )
    {
        return rpc.RequestAsync<TRequest, TResponse>(request, _ => { }, cancellationToken);
    }

    /// <summary>
    ///     Makes an RPC style request
    /// </summary>
    /// <typeparam name="TRequest">The request type</typeparam>
    /// <typeparam name="TResponse">The response type</typeparam>
    /// <param name="rpc">The rpc instance</param>
    /// <param name="request">The request message</param>
    /// <param name="configure">
    ///     Fluent configuration e.g. x => x.WithQueueName("uk.london")
    /// </param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <returns>The response</returns>
    public static TResponse Request<TRequest, TResponse>(
        this IRpc rpc,
        TRequest request,
        Action<IRequestConfiguration> configure,
        CancellationToken cancellationToken = default
    )
    {
        return rpc.RequestAsync<TRequest, TResponse>(request, configure, cancellationToken)
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>
    ///     Set up a responder for an RPC service.
    /// </summary>
    /// <typeparam name="TRequest">The request type</typeparam>
    /// <typeparam name="TResponse">The response type</typeparam>
    /// <param name="rpc">The rpc instance</param>
    /// <param name="responder">A function that performs the response</param>
    /// <param name="cancellationToken">The cancellation token</param>
    public static Task<IDisposable> RespondAsync<TRequest, TResponse>(
        this IRpc rpc,
        Func<TRequest, TResponse> responder,
        CancellationToken cancellationToken = default
    )
    {
        var asyncResponder = TaskHelpers.FromFunc<TRequest, TResponse>((m, _) => responder(m));
        return rpc.RespondAsync(asyncResponder, _ => { }, cancellationToken);
    }

    /// <summary>
    ///     Set up a responder for an RPC service.
    /// </summary>
    /// <typeparam name="TRequest">The request type</typeparam>
    /// <typeparam name="TResponse">The response type</typeparam>
    /// <param name="rpc">The rpc instance</param>
    /// <param name="responder">A function that performs the response</param>
    /// <param name="cancellationToken">The cancellation token</param>
    public static Task<IDisposable> RespondAsync<TRequest, TResponse>(
        this IRpc rpc,
        Func<TRequest, Task<TResponse>> responder,
        CancellationToken cancellationToken = default
    ) => rpc.RespondAsync<TRequest, TResponse>((r, _) => responder(r), _ => { }, cancellationToken);

    /// <summary>
    ///     Set up a responder for an RPC service.
    /// </summary>
    /// <typeparam name="TRequest">The request type</typeparam>
    /// <typeparam name="TResponse">The response type</typeparam>
    /// <param name="rpc">The rpc instance</param>
    /// <param name="responder">A function that performs the response</param>
    /// <param name="configure">A function that performs the configuration</param>
    /// <param name="cancellationToken">The cancellation token</param>
    public static Task<IDisposable> RespondAsync<TRequest, TResponse>(
        this IRpc rpc,
        Func<TRequest, CancellationToken, Task<TResponse>> responder,
        Action<IResponderConfiguration> configure,
        CancellationToken cancellationToken = default
    ) => rpc.RespondAsync<TRequest, TResponse>((request, _, token) => responder(request, token), configure, cancellationToken);

    /// <summary>
    ///     Set up a responder for an RPC service.
    /// </summary>
    /// <typeparam name="TRequest">The request type</typeparam>
    /// <typeparam name="TResponse">The response type</typeparam>
    /// <param name="rpc">The rpc instance</param>
    /// <param name="responder">A function that performs the response</param>
    /// <param name="cancellationToken">The cancellation token</param>
    public static IDisposable Respond<TRequest, TResponse>(
        this IRpc rpc,
        Func<TRequest, Task<TResponse>> responder,
        CancellationToken cancellationToken = default
    ) => rpc.Respond(responder, _ => { }, cancellationToken);

    /// <summary>
    ///     Set up a responder for an RPC service.
    /// </summary>
    /// <typeparam name="TRequest">The request type</typeparam>
    /// <typeparam name="TResponse">The response type</typeparam>
    /// <param name="rpc">The rpc instance</param>
    /// <param name="responder">A function that performs the response</param>
    /// <param name="cancellationToken">The cancellation token</param>
    public static IDisposable Respond<TRequest, TResponse>(
        this IRpc rpc,
        Func<TRequest, TResponse> responder,
        CancellationToken cancellationToken = default
    ) => rpc.Respond(responder, _ => { }, cancellationToken);

    /// <summary>
    ///     Set up a responder for an RPC service.
    /// </summary>
    /// <typeparam name="TRequest">The request type</typeparam>
    /// <typeparam name="TResponse">The response type</typeparam>
    /// <param name="rpc">The rpc instance</param>
    /// <param name="responder">A function that performs the response</param>
    /// <param name="configure">A function that performs the configuration</param>
    /// <param name="cancellationToken">The cancellation token</param>
    public static IDisposable Respond<TRequest, TResponse>(
        this IRpc rpc,
        Func<TRequest, TResponse> responder,
        Action<IResponderConfiguration> configure,
        CancellationToken cancellationToken = default
    )
    {
        var asyncResponder = TaskHelpers.FromFunc<TRequest, TResponse>((m, _) => responder(m));
        return rpc.Respond(asyncResponder, configure, cancellationToken);
    }


    /// <summary>
    ///     Set up a responder for an RPC service.
    /// </summary>
    /// <typeparam name="TRequest">The request type</typeparam>
    /// <typeparam name="TResponse">The response type</typeparam>
    /// <param name="rpc">The rpc instance</param>
    /// <param name="responder">A function that performs the response</param>
    /// <param name="configure">A function that performs the configuration</param>
    /// <param name="cancellationToken">The cancellation token</param>
    public static IDisposable Respond<TRequest, TResponse>(
        this IRpc rpc,
        Func<TRequest, Task<TResponse>> responder,
        Action<IResponderConfiguration> configure,
        CancellationToken cancellationToken = default
    ) => rpc.Respond<TRequest, TResponse>((r, _) => responder(r), configure, cancellationToken);

    /// <summary>
    ///     Set up a responder for an RPC service.
    /// </summary>
    /// <typeparam name="TRequest">The request type</typeparam>
    /// <typeparam name="TResponse">The response type</typeparam>
    /// <param name="rpc">The rpc instance</param>
    /// <param name="responder">A function that performs the response</param>
    /// <param name="configure">A function that performs the configuration</param>
    /// <param name="cancellationToken">The cancellation token</param>
    public static IDisposable Respond<TRequest, TResponse>(
        this IRpc rpc,
        Func<TRequest, CancellationToken, Task<TResponse>> responder,
        Action<IResponderConfiguration> configure,
        CancellationToken cancellationToken = default
    )
    {
        return rpc.RespondAsync(responder, configure, cancellationToken)
            .GetAwaiter()
            .GetResult();
    }
}
