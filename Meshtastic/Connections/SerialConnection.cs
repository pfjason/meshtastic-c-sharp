using Google.Protobuf;
using Meshtastic.Data;
using Meshtastic.Protobufs;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.IO.Ports;

namespace Meshtastic.Connections;

public class SerialConnection : DeviceConnection
{
    private readonly SerialPort serialPort;

    public SerialConnection(ILogger logger, string port, int baudRate = Resources.DEFAULT_BAUD_RATE) : base(logger)
    {
        serialPort = new SerialPort(port, baudRate)
        {
            DtrEnable = true,
            Handshake = Handshake.XOnXOff,
            WriteBufferSize = 8,
        };
    }

    public SerialConnection(ILogger logger, 
        string port, 
        DeviceStateContainer container, 
        bool dtrEnable = true, 
        Handshake handshake = Handshake.XOnXOff, 
        int baudRate = Resources.DEFAULT_BAUD_RATE) : base(logger)
    {
        serialPort = new SerialPort(port, baudRate)
        {
            DtrEnable = dtrEnable,
            Handshake = handshake,
            WriteBufferSize = 8,
        };
        DeviceStateContainer = container;
    }

    public static string[] ListPorts() => SerialPort.GetPortNames();

    public override Task Monitor()
    {
        Logger.LogDebug("Opening serial port...");
        serialPort.Open();
        while (serialPort.IsOpen)
        {
            if (serialPort.BytesToRead > 0)
            {
                var line = serialPort.ReadLine();
                if (line.Contains("INFO  |"))
                    Logger.LogInformation(line);
                else if (line.Contains("WARN  |"))
                    Logger.LogWarning(line);
                else if (line.Contains("DEBUG |"))
                    Logger.LogDebug(line);
                else if (line.Contains("ERROR |"))
                    Logger.LogError(line);
                else
                    Logger.LogInformation(line);
            }
           // await Task.Delay(10);
           
        }
        Logger.LogDebug("Disconnected from serial");
        return Task.CompletedTask;
    }

    public override async Task<DeviceStateContainer> WriteToRadio(ToRadio packet, Func<FromRadio, DeviceStateContainer, Task<bool>> isComplete)
    {
        //await Task.Delay(1000);
        DeviceStateContainer.AddToRadio(packet);
        var toRadio = PacketFraming.CreatePacket(packet.ToByteArray());
        if (!serialPort.IsOpen)
            serialPort.Open();
        await serialPort.BaseStream.WriteAsync(PacketFraming.SERIAL_PREAMBLE.AsMemory(0, PacketFraming.SERIAL_PREAMBLE.Length));
        await serialPort.BaseStream.WriteAsync(toRadio);
        VerboseLogPacket(packet);
        await ReadFromRadio(isComplete);
        return DeviceStateContainer;
    }

    public override void Disconnect()
    {
        serialPort.Close();
    }

    public override async Task WriteToRadio(ToRadio packet)
    {
        DeviceStateContainer.AddToRadio(packet);
        var toRadio = PacketFraming.CreatePacket(packet.ToByteArray());
        await serialPort.BaseStream.WriteAsync(toRadio);
        await serialPort.BaseStream.FlushAsync();
        VerboseLogPacket(packet);
    }

    public override async Task ReadFromRadio(Func<FromRadio, DeviceStateContainer, Task<bool>> isComplete, int readTimeoutMs = Resources.DEFAULT_READ_TIMEOUT)
    {
        var sw = new Stopwatch();

        while (serialPort.IsOpen && sw.ElapsedMilliseconds < readTimeoutMs)
        {
            if (serialPort.BytesToRead == 0)
            {
                await Task.Delay(10);
                continue;
            }
            var buffer = new byte[1];
            await serialPort.BaseStream.ReadAsync(buffer);
            if (await ParsePackets(buffer.First(), isComplete))
                return;
        }
    }
}