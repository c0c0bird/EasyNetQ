using EasyNetQ.Events;
using EasyNetQ.Internals;
using EasyNetQ.Logging;
using EasyNetQ.Persistent;
using EasyNetQ.Topology;
using System.Collections.Concurrent;
using System.Text;

namespace EasyNetQ;

/// <summary>
///     Default implementation of EasyNetQ's request-response pattern
/// </summary>
public class DefaultRpc : IRpc, IDisposable
{
    protected const string IsFaultedKey = "IsFaulted";
    protected const string ExceptionMessageKey = "ExceptionMessage";
    protected readonly IAdvancedBus advancedBus;
    private readonly ILogger<DefaultRpc> logger;
    private readonly ConnectionConfiguration configuration;
    protected readonly IConventions conventions;
    private readonly ICorrelationIdGenerationStrategy correlationIdGenerationStrategy;
    private readonly IDisposable eventSubscription;
    protected readonly IExchangeDeclareStrategy exchangeDeclareStrategy;

    protected readonly IMessageDeliveryModeStrategy messageDeliveryModeStrategy;

    private readonly ConcurrentDictionary<string, ResponseAction> responseActions = new();

    private readonly ConcurrentDictionary<RpcKey, ResponseSubscription> responseSubscriptions = new();

    private readonly AsyncLock responseSubscriptionsLock = new();
    private readonly ITypeNameSerializer typeNameSerializer;

    public DefaultRpc(
        ILogger<DefaultRpc> logger,
        ConnectionConfiguration configuration,
        IAdvancedBus advancedBus,
        IEventBus eventBus,
        IConventions conventions,
        IExchangeDeclareStrategy exchangeDeclareStrategy,
        IMessageDeliveryModeStrategy messageDeliveryModeStrategy,
        ITypeNameSerializer typeNameSerializer,
        ICorrelationIdGenerationStrategy correlationIdGenerationStrategy
    )
    {
        this.logger = logger;
        this.configuration = configuration;
        this.advancedBus = advancedBus;
        this.conventions = conventions;
        this.exchangeDeclareStrategy = exchangeDeclareStrategy;
        this.messageDeliveryModeStrategy = messageDeliveryModeStrategy;
        this.typeNameSerializer = typeNameSerializer;
        this.correlationIdGenerationStrategy = correlationIdGenerationStrategy;

        eventSubscription = eventBus.Subscribe<ConnectionRecoveredEvent>(OnConnectionRecovered);
    }

    /// <inheritdoc />
    public virtual async Task<TResponse> RequestAsync<TRequest, TResponse>(
        TRequest request,
        Action<IRequestConfiguration> configure,
        CancellationToken cancellationToken = default
    )
    {
        var requestType = typeof(TRequest);
        var requestConfiguration = new RequestConfiguration(
            conventions.RpcRoutingKeyNamingConvention(requestType),
            configuration.Timeout
        );
        configure(requestConfiguration);

        using var cts = cancellationToken.WithTimeout(requestConfiguration.Expiration);

        var correlationId = correlationIdGenerationStrategy.GetCorrelationId();
        var tcs = new TaskCompletionSource<TResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        RegisterResponseActions(correlationId, tcs);
        using var callback = DisposableAction.Create(DeRegisterResponseActions, correlationId);

        var queueName = await SubscribeToResponseAsync<TRequest, TResponse>(cts.Token).ConfigureAwait(false);
        var routingKey = requestConfiguration.QueueName;
        var expiration = requestConfiguration.Expiration;
        var priority = requestConfiguration.Priority;
        var headers = requestConfiguration.MessageHeaders;
        await RequestPublishAsync(
            request,
            routingKey,
            queueName,
            correlationId,
            expiration,
            priority,
            null,
            requestConfiguration.PublisherConfirms,
            headers,
            cts.Token
        ).ConfigureAwait(false);
        tcs.AttachCancellation(cts.Token);
        return await tcs.Task.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public virtual Task<IDisposable> RespondAsync<TRequest, TResponse>(
        Func<TRequest, IDictionary<string, object?>?, CancellationToken, Task<TResponse>> responder,
        Action<IResponderConfiguration> configure,
        CancellationToken cancellationToken = default
    )
    {
        // We're explicitly validating TResponse here because the type won't be used directly.
        // It'll only be used when executing a successful responder, which will silently fail if TResponse serialized length exceeds the limit.
        var serializedResponse = typeNameSerializer.Serialize(typeof(TResponse));
        if (serializedResponse.Length > 255)
            throw new ArgumentOutOfRangeException(nameof(TResponse), typeof(TResponse), "Must be less than or equal to 255 characters when serialized.");

        return RespondAsyncInternal(responder, configure, cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        eventSubscription.Dispose();
        foreach (var responseSubscription in responseSubscriptions.Values)
            responseSubscription.Unsubscribe();
    }

    private void OnConnectionRecovered(in ConnectionRecoveredEvent @event)
    {
        if (@event.Type != PersistentConnectionType.Consumer)
            return;

        var responseActionsValues = responseActions.Values;
        var responseSubscriptionsValues = responseSubscriptions.Values;

        responseActions.Clear();
        responseSubscriptions.Clear();

        foreach (var responseAction in responseActionsValues) responseAction.OnFailure();
        foreach (var responseSubscription in responseSubscriptionsValues) responseSubscription.Unsubscribe();
    }

    protected void DeRegisterResponseActions(string correlationId)
    {
        responseActions.Remove(correlationId);
    }

    protected void RegisterResponseActions<TResponse>(string correlationId, TaskCompletionSource<TResponse> tcs)
    {
        var responseAction = new ResponseAction(
            message =>
            {
                var messageOfTResponse = (IMessage<TResponse>)message;

                var isFaulted = false;
                var exceptionMessage = "The exception message has not been specified.";

                if (messageOfTResponse.Properties is { HeadersPresent: true, Headers: not null })
                {
                    if (messageOfTResponse.Properties.Headers.TryGetValue(IsFaultedKey, out var isFaultedValue))
                        isFaulted = Convert.ToBoolean(isFaultedValue);
                    if (messageOfTResponse.Properties.Headers.TryGetValue(ExceptionMessageKey, out var exchangeMessageValue))
                        exceptionMessage = Encoding.UTF8.GetString((byte[])exchangeMessageValue!);
                }

                if (isFaulted)
                    tcs.TrySetException(new EasyNetQResponderException(exceptionMessage));
                else
                    tcs.TrySetResult(messageOfTResponse.Body!);
            },
            () => tcs.TrySetException(
                new EasyNetQException(
                    $"Connection lost while request was in-flight. CorrelationId: {correlationId}"
                )
            )
        );

        responseActions.TryAdd(correlationId, responseAction);
    }

    protected virtual async Task<string> SubscribeToResponseAsync<TRequest, TResponse>(
        CancellationToken cancellationToken
    )
    {
        var responseType = typeof(TResponse);
        var requestType = typeof(TRequest);
        var rpcKey = new RpcKey(requestType, responseType);
        if (responseSubscriptions.TryGetValue(rpcKey, out var responseSubscription))
            return responseSubscription.QueueName;

        logger.Debug("Subscribing for {requestType}/{responseType}", requestType, responseType);

        using var _ = await responseSubscriptionsLock.AcquireAsync(cancellationToken).ConfigureAwait(false);

        if (responseSubscriptions.TryGetValue(rpcKey, out responseSubscription))
            return responseSubscription.QueueName;

        var queue = await advancedBus.QueueDeclareAsync(
            conventions.RpcReturnQueueNamingConvention(responseType),
            durable: false,
            exclusive: true,
            autoDelete: true,
            cancellationToken: cancellationToken
        ).ConfigureAwait(false);

        var exchangeName = conventions.RpcResponseExchangeNamingConvention(responseType);
        if (exchangeName != Exchange.Default.Name)
        {
            var exchange = await exchangeDeclareStrategy.DeclareExchangeAsync(
                exchangeName, ExchangeType.Direct, cancellationToken
            ).ConfigureAwait(false);
            await advancedBus.BindAsync(exchange, queue, queue.Name, cancellationToken).ConfigureAwait(false);
        }

        var subscription = advancedBus.Consume<TResponse>(
            queue,
            (message, _) =>
            {
                if (message.Properties.CorrelationId != null && responseActions.TryRemove(message.Properties.CorrelationId, out var responseAction))
                    responseAction.OnSuccess(message);
            }
        );
        responseSubscriptions.TryAdd(rpcKey, new ResponseSubscription(queue.Name, subscription));

        logger.Debug("Subscription for {requestType}/{responseType} is created", requestType, responseType);

        return queue.Name;
    }

    protected virtual async Task RequestPublishAsync<TRequest>(
        TRequest request,
        string routingKey,
        string returnQueueName,
        string correlationId,
        TimeSpan expiration,
        byte? priority,
        bool? mandatory,
        bool? publisherConfirms,
        IDictionary<string, object?>? headers,
        CancellationToken cancellationToken
    )
    {
        var requestType = typeof(TRequest);
        var exchange = await exchangeDeclareStrategy.DeclareExchangeAsync(
            conventions.RpcRequestExchangeNamingConvention(requestType),
            ExchangeType.Direct,
            cancellationToken
        ).ConfigureAwait(false);

        var properties = new MessageProperties
        {
            ReplyTo = returnQueueName,
            CorrelationId = correlationId,
            Priority = priority ?? 0,
            Headers = headers,
            DeliveryMode = messageDeliveryModeStrategy.GetDeliveryMode(requestType),
            Expiration = expiration == Timeout.InfiniteTimeSpan ? null : expiration
        };

        var requestMessage = new Message<TRequest>(request, properties);
        await advancedBus.PublishAsync(exchange.Name, routingKey, mandatory, publisherConfirms, requestMessage, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<IDisposable> RespondAsyncInternal<TRequest, TResponse>(
        Func<TRequest, IDictionary<string, object?>?, CancellationToken, Task<TResponse>> responder,
        Action<IResponderConfiguration> configure,
        CancellationToken cancellationToken
    )
    {
        var requestType = typeof(TRequest);

        var responderConfiguration = new ResponderConfiguration(configuration.PrefetchCount, conventions.QueueTypeConvention(typeof(TRequest)));
        configure(responderConfiguration);

        var routingKey = responderConfiguration.QueueName ?? conventions.RpcRoutingKeyNamingConvention(requestType);

        var exchange = await advancedBus.ExchangeDeclareAsync(
            exchange: conventions.RpcRequestExchangeNamingConvention(requestType),
            type: ExchangeType.Direct,
            cancellationToken: cancellationToken
        ).ConfigureAwait(false);

        var queue = await advancedBus.QueueDeclareAsync(
            queue: routingKey,
            durable: responderConfiguration.Durable,
            arguments: responderConfiguration.QueueArguments,
            cancellationToken: cancellationToken
        ).ConfigureAwait(false);

        await advancedBus.BindAsync(exchange, queue, routingKey, cancellationToken).ConfigureAwait(false);

        return advancedBus.Consume<TRequest>(
            queue,
            (message, _, cancellation) => RespondToMessageAsync(responder, message, cancellation),
            c => c.WithPrefetchCount(responderConfiguration.PrefetchCount)
        );
    }

    private async Task RespondToMessageAsync<TRequest, TResponse>(
        Func<TRequest, IDictionary<string, object?>?, CancellationToken, Task<TResponse>> responder,
        IMessage<TRequest> requestMessage,
        CancellationToken cancellationToken
    )
    {
        var responseExchangeName = conventions.RpcResponseExchangeNamingConvention(typeof(TResponse));
        var responseExchange = responseExchangeName == Exchange.Default.Name
            ? Exchange.Default
            : await exchangeDeclareStrategy.DeclareExchangeAsync(
                responseExchangeName,
                ExchangeType.Direct,
                cancellationToken: cancellationToken
            ).ConfigureAwait(false);

        try
        {
            var request = requestMessage.Body!;
            var response = await responder(request, requestMessage.Properties.Headers, cancellationToken).ConfigureAwait(false);
            var responseMessage = new Message<TResponse>(
                response,
                new MessageProperties
                {
                    CorrelationId = requestMessage.Properties.CorrelationId,
                    DeliveryMode = MessageDeliveryMode.NonPersistent
                }
            );
            await advancedBus.PublishAsync(
                responseExchange.Name,
                requestMessage.Properties.ReplyTo!,
                false,
                null,
                responseMessage,
                cancellationToken
            ).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var responseMessage = new Message<TResponse>(
                default,
                new MessageProperties
                {
                    CorrelationId = requestMessage.Properties.CorrelationId,
                    DeliveryMode = MessageDeliveryMode.NonPersistent,
                    Headers = new Dictionary<string, object?>
                    {
                        { IsFaultedKey, true },
                        { ExceptionMessageKey, Encoding.UTF8.GetBytes(exception.Message) }
                    }
                }
            );
            await advancedBus.PublishAsync(
                responseExchange.Name,
                requestMessage.Properties.ReplyTo!,
                false,
                null,
                responseMessage,
                cancellationToken
            ).ConfigureAwait(false);

            throw;
        }
    }

    protected readonly record struct RpcKey(Type RequestType, Type ResponseType);

    protected readonly struct ResponseAction
    {
        public ResponseAction(Action<object> onSuccess, Action onFailure)
        {
            OnSuccess = onSuccess;
            OnFailure = onFailure;
        }

        public Action<object> OnSuccess { get; }
        public Action OnFailure { get; }
    }

    protected readonly struct ResponseSubscription
    {
        public ResponseSubscription(string queueName, IDisposable subscription)
        {
            QueueName = queueName;
            Unsubscribe = subscription.Dispose;
        }

        public string QueueName { get; }
        public Action Unsubscribe { get; }
    }
}
