namespace EduSphere.Safety.Shared;
public interface ICommand<TResponse> { }
public interface ICommandHandler<TCommand, TResponse> where TCommand : ICommand<TResponse>
{
    Task<Result<TResponse>> HandleAsync(TCommand command, CancellationToken ct);
}
public interface ISender
{
    Task<Result<TResponse>> SendAsync<TResponse>(ICommand<TResponse> command, CancellationToken ct = default);
}
public sealed class Sender(IServiceProvider services) : ISender
{
    public async Task<Result<TResponse>> SendAsync<TResponse>(ICommand<TResponse> command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var handlerType = typeof(ICommandHandler<,>).MakeGenericType(command.GetType(), typeof(TResponse));
        dynamic handler = services.GetService(handlerType)
            ?? throw new InvalidOperationException($"No handler for {command.GetType().Name} (expected {handlerType.FullName}).");
        return await handler.HandleAsync((dynamic)command, ct);
    }
}
