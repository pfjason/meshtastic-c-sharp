using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using Meshtastic.Data;
using Meshtastic.Data.MessageFactories;
using Meshtastic.Extensions;
using Meshtastic.Protobufs;
using Microsoft.Extensions.Logging;

namespace Meshtastic.Connections;

public class MessageRecievedEventArgs : EventArgs
{
    #nullable disable
    public DeviceStateContainer DeviceStateContainer {get; set;}
    public FromRadio Message;
    #nullable enable

    public MessageRecievedEventArgs()
    {
    }
}

public abstract class DeviceConnection(ILogger logger) : IDisposable
{
    public event EventHandler<MessageRecievedEventArgs> MessageRecieved;

    private ConcurrentQueue<ToRadio> SendQueue = new();
    private Task QueueTask = null;
    private Task ListenTask = null;
    protected CancellationTokenSource ShowStopper = new();
    private bool disposedValue;

    protected ILogger Logger { get; set; } = logger;
    public ToRadioMessageFactory ToRadioFactory { get; private set; } = new ToRadioMessageFactory();
    public DeviceStateContainer DeviceStateContainer { get; set; } = new DeviceStateContainer();
    protected List<byte> Buffer { get; set; } = [];
    protected int PacketLength { get; set; }

    public DeviceConnection(ILogger logger, DeviceStateContainer container) : this(logger)
    {
        DeviceStateContainer = container;
    }

    public virtual Task Monitor()
    {
        throw new NotImplementedException();
    }

    public void Send(ToRadio toRadio)
    {
        SendQueue.Enqueue(toRadio);

        if (QueueTask == null || QueueTask.IsCompleted || QueueTask.IsFaulted)
        {
            try
            {
                QueueTask = Task.Run(async () =>
                {
                    while (SendQueue.TryDequeue(out var item) && !ShowStopper.IsCancellationRequested)
                    {
                        await WriteToRadio(toRadio);
                    }
                }, ShowStopper.Token);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error sending message: {ex.Message}");
            }
        }
    }

    protected abstract Task<DeviceStateContainer> WriteToRadio(ToRadio toRadio, Func<FromRadio, DeviceStateContainer, Task<bool>> isComplete);

    protected abstract Task WriteToRadio(ToRadio toRadio);

    public abstract void Disconnect();

    public async Task Start()
    {
        ShowStopper.TryReset();

        if (ListenTask == null || ListenTask.IsCompleted)
        {
            var messageFactory = new ToRadioMessageFactory();
            var wantConfig = messageFactory.CreateWantConfigMessage();
            await WriteToRadio(wantConfig);

            ListenTask = Task.Run(() =>
            {
                while (!ShowStopper.IsCancellationRequested)
                {
                    ReadFromRadio((fromRadio, deviceStateContainer) => 
                    {
                        MessageRecieved?.Invoke(this, new MessageRecievedEventArgs(){
                            Message = fromRadio,
                             DeviceStateContainer = deviceStateContainer
                        });
                        return Task.FromResult(true);
                    });
                }                
            }, ShowStopper.Token);
        }
    }

    public async Task Stop()
    {
        ShowStopper.Cancel();
        await Task.WhenAll((new Task[] { ListenTask, QueueTask }).Where(t => t != null));
    }

    protected abstract Task ReadFromRadio(Func<FromRadio?, DeviceStateContainer, Task<bool>> isComplete,
        int readTimeoutMs = Resources.DEFAULT_READ_TIMEOUT);

    protected async Task<bool> ParsePackets(byte item, Func<FromRadio, DeviceStateContainer, Task<bool>> isComplete)
    {
        int bufferIndex = Buffer.Count;
        Buffer.Add(item);
        if (bufferIndex == 0 && item != PacketFraming.PACKET_FRAME_START[0])
            Buffer.Clear();
        else if (bufferIndex == 1 && item != PacketFraming.PACKET_FRAME_START[1])
            Buffer.Clear();
        else if (bufferIndex >= PacketFraming.PACKET_HEADER_LENGTH - 1)
        {
            PacketLength = (Buffer[2] << 8) + Buffer[3];
            if (bufferIndex == PacketFraming.PACKET_HEADER_LENGTH - 1 && PacketLength > Resources.MAX_TO_FROM_RADIO_LENGTH)
            {
                Logger.LogTrace("Packet failed size validation");
                Buffer.Clear();
            }

            if (Buffer.Count > 0 && (bufferIndex + 1) >= (PacketLength + PacketFraming.PACKET_HEADER_LENGTH))
            {
                var payload = Buffer.Skip(PacketFraming.PACKET_HEADER_LENGTH).ToArray();
                var message = new FromDeviceMessage(Logger);
                var fromRadio = message.ParsedFromRadio(payload);

                if (fromRadio != null)
                {
                    DeviceStateContainer.AddFromRadio(fromRadio);
                    Logger.LogDebug($"Received: {fromRadio}");
                    // Use telemetry packets as a cue to keep the connection alive
                    if (fromRadio.GetPayload<Telemetry>() != null)
                    {
                        Logger.LogDebug($"Sending heartbeat");
                        await WriteToRadio(ToRadioFactory.CreateKeepAliveMessage());
                    }

                    if (await isComplete(fromRadio, DeviceStateContainer))
                    {
                        Buffer.Clear();
                        return true;
                    }
                }
                else
                {
                    Logger.LogDebug($"Notification of pending packets {Convert.ToBase64String(payload)}");
                }
                Buffer.Clear();
            }
        }
        return false;
    }

    protected void VerboseLogPacket(ToRadio toRadio)
    {
        Logger.LogDebug($"Sent: {toRadio}");
        var payload = toRadio.GetPayload<AdminMessage>()?.ToString() ??
            toRadio.GetPayload<XModem>()?.ToString() ??
            toRadio.GetPayload<NodeInfo>()?.ToString() ??
            toRadio.GetPayload<Position>()?.ToString() ??
            toRadio.GetPayload<Waypoint>()?.ToString() ??
            toRadio.GetPayload<Telemetry>()?.ToString() ??
            toRadio.GetPayload<Routing>()?.ToString() ??
            toRadio.GetPayload<RouteDiscovery>()?.ToString() ??
            toRadio.GetPayload<TAKPacket>()?.ToString() ??
            toRadio.GetPayload<Neighbor>()?.ToString() ??
            toRadio.GetPayload<string>()?.ToString();

        if (!String.IsNullOrWhiteSpace(payload))
        {
            Logger.LogDebug($"Payload decoded: {payload}");
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                ShowStopper.Dispose();
            }

            disposedValue = true;
        }
    }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}