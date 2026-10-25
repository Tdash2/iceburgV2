
using System.Collections.Concurrent;
using System.Threading.Channels;

public static class TerminalOutputHub
{
    private static readonly ConcurrentDictionary<
        Guid,
        Channel<string>> Clients = new();

    public static void Broadcast(string message)
    {
        foreach (var client in Clients.Values)
        {
            client.Writer.TryWrite(message);
        }
    }

    public static ChannelReader<string> Subscribe(out Guid id)
    {
        id = Guid.NewGuid();

        var channel = Channel.CreateBounded<string>(
            new BoundedChannelOptions(200)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });

        Clients[id] = channel;

        return channel.Reader;
    }

    public static void Unsubscribe(Guid id)
    {
        if (Clients.TryRemove(id, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }
}
