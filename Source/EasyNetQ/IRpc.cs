using EasyNetQ.Internals;
using RabbitMQ.Client;

namespace EasyNetQ;

/// <summary>
///     An RPC style request-response pattern
/// </summary>
public interface IRpc
{
    /// <summary>
    ///     Make a request to an RPC service
    /// </summary>
    /// <typeparam name="TRequest">The request type</typeparam>
    /// <typeparam name="TResponse">The response type</typeparam>
    /// <param name="request">The request message</param>
    /// <param name="configure">
    ///     Fluent configuration e.g. x => x.WithQueueName("uk.london")
    /// </param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <returns>Returns a task that yields the result when the response arrives</returns>
    Task<TResponse> RequestAsync<TRequest, TResponse>(
        TRequest request,
        Action<IRequestConfiguration> configure,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Set up a responder for an RPC service that uses the message headers.
    /// </summary>
    /// <typeparam name="TRequest">The request type</typeparam>
    /// <typeparam name="TResponse">The response type</typeparam>
    /// <param name="responder">
    ///     A function that returns the response and accepts three input parameters:
    ///     TRequest: the request body,
    ///     IDictionary: the request headers,
    ///     CancellationToken: a cancellation token.
    /// </param>
    /// <param name="configure">A function that performs the configuration</param>
    /// <param name="cancellationToken">The cancellation token</param>
    Task<IDisposable> RespondAsync<TRequest, TResponse>(
        Func<TRequest, IDictionary<string, object?>?, CancellationToken, Task<TResponse>> responder,
        Action<IResponderConfiguration> configure,
        CancellationToken cancellationToken = default
    );
}
