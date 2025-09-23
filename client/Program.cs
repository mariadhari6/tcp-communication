using System;
using System.Text;
using System.Net.Sockets;
using DotNetEnv;
using Serilog;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

public class Program
{
  private static readonly string TEXT = """
  Hello Morasaurus
  Lorem ipsum dolor sit amet, consectetur adipiscing elit. Donec eget laoreet eros.
  Halo Dunia
  Hello Dinosaurus
  """;

  private static readonly byte ENQ = 5;
  private static readonly byte ACK = 6;
  private static readonly byte ETB = 23;
  private static readonly byte EOT = 4;
  private static readonly byte STX = 2;
  private static readonly byte ETX = 3;
  private static readonly byte CR = 13;
  private static readonly byte LF = 10;
  private static readonly byte NAK = 21;

  private static readonly int MAX_SEQUENCE = 7;
  private static readonly int maxCharactersPerLine = 50;

  private static int line = 0;
  private static int partition = 0;
  private static int lastPartition = partition;
  private static int sequence = 1;
  private static bool running = true;

  private static TcpClient client;
  private static NetworkStream stream;

  public static void Main(string[] args)
  {
    Log.Logger = new LoggerConfiguration()
      .MinimumLevel.Information()
      .WriteTo.Console()
      .WriteTo.File("logs/myapp-client-.txt", rollingInterval: RollingInterval.Day)
      .CreateLogger();

    Env.Load();
    string host = Environment.GetEnvironmentVariable("TCP_HOST") ?? "127.0.0.1";
    int port = int.Parse(Environment.GetEnvironmentVariable("TCP_PORT") ?? "5000");

    try
    {
      client = new TcpClient(host, port);
      stream = client.GetStream();
      Console.WriteLine($"Connected to TCP Server {host}:{port}");

      // Send ENQ first
      stream.Write(new byte[] { ENQ });
      Console.WriteLine("Send ENQ to Server.");

      // Thread to receive responses
      Thread t = new Thread(ReceiveHandler);
      t.Start();

      while (running)
      {
        Thread.Sleep(100);
      }

      client.Close();
    }
    catch (Exception e)
    {
      Console.WriteLine("Error: " + e.Message);
    }
  }

  private static void Reset()
  {
    partition = 0;
    lastPartition = partition;
    sequence = 1;
    line = 0;
  }

  private static void LogPacket(byte[] packet)
  {
    string packetString = Encoding.Latin1.GetString(packet);
    Log.Information("Packet String: " + packetString
      .Replace("\r", "<CR>").Replace("\n", "<LF>")
      .Replace(((char)STX).ToString(), "<STX>")
      .Replace(((char)ETX).ToString(), "<ETX>")
      .Replace(((char)ETB).ToString(), "<ETB>"));
  }

  private static string? GetText()
  {
    string[] lines = TEXT.Split('\n');
    if (line < lines.Length)
    {
      lastPartition = partition;
      string text;
      if (lines[line].Length <= maxCharactersPerLine)
      {
        text = lines[line];
        line++;
        partition = 0;
      }
      else
      {
        int totalPartition = (int)Math.Ceiling((double)lines[line].Length / maxCharactersPerLine);
        int startIndex = partition * maxCharactersPerLine;
        text = lines[line].Substring(startIndex, Math.Min(maxCharactersPerLine, lines[line].Length - startIndex));
        partition++;
        Console.WriteLine($"Total partition: {totalPartition}, Current: {partition}");
        if (partition >= totalPartition)
        {
          line++;
          partition = 0;
        }
      }
      return text;
    }
    return null;
  }

  private static string GenerateCheckSum(byte[] packet, byte endtx)
  {
    int indexSTX = Array.IndexOf(packet, STX);
    int indexENDTX = Array.LastIndexOf(packet, endtx);

    if (indexSTX == -1 || indexENDTX == -1 || indexENDTX <= indexSTX)
      throw new ArgumentException("Invalid packet format");

    byte[] contentBytes = packet[(indexSTX + 1)..(indexENDTX + 1)];
    int total = contentBytes.Sum(c => (int)c);
    return (total % 256).ToString("X2");
  }

  private static bool IsValidChecksum(byte[] packet, byte endtx)
  {
    if (packet[^2] != CR || packet[^1] != LF) return false;

    int indexENDTX = Array.IndexOf(packet, endtx);
    List<byte> checkSumBytes = new(packet[(indexENDTX + 1)..(indexENDTX + 3)]);

    string receivedCheckSum = Encoding.Latin1.GetString(checkSumBytes.ToArray());
    string calculatedCheckSum = GenerateCheckSum(packet, endtx);

    if (!receivedCheckSum.Equals(calculatedCheckSum, StringComparison.OrdinalIgnoreCase))
    {
      Console.WriteLine(endtx == ETX ? "Error in ETX packet" : "Error in ETB packet");
      Log.Information("=== Checksum Error ===");
      Log.Information("Received: " + receivedCheckSum);
      Log.Information("Calculated: " + calculatedCheckSum);
    }

    return receivedCheckSum.Equals(calculatedCheckSum, StringComparison.OrdinalIgnoreCase);
  }

  private static void ReceiveHandler()
  {
    byte[] buffer = new byte[1024];
    try
    {
      while ((stream.Read(buffer, 0, buffer.Length)) > 0)
      {
        int data = buffer[0]; // baca byte pertama (karena biasanya 1 byte: ACK/NAK/EOT)
        if (data == ACK)
        {
          Console.WriteLine("Received ACK from Server.");
          SendText();
        }
        else if (data == NAK)
        {
          Console.WriteLine("Received NAK from Server.");
          sequence = sequence == 0 ? MAX_SEQUENCE : sequence - 1;
          if (partition == 0 && line > 0)
            line = Math.Max(0, line - 1);
          partition = lastPartition;
          SendText();
        }
        else
        {
          Console.WriteLine("Received unknown data: " + data);
        }
      }
    }
    catch (Exception ex)
    {
      Console.WriteLine("Receive error: " + ex.Message);
    }
  }

  private static void SendText()
  {
    string? text = GetText();
    Console.WriteLine($"Partition: {partition}, Sequence: {sequence}");

    if (text == null)
    {
      Console.WriteLine("All text sent.");
      stream.Write(new byte[] { EOT });
      Console.WriteLine("Send EOT to Server.");
      Reset();
      running = false;
      return;
    }

    List<byte> packet;
    byte[] content = Encoding.Latin1.GetBytes(text);
    string checkSum;
    bool simulationMode = Environment.GetEnvironmentVariable("SIMULATION_MODE") == "true";
    byte currentSequence = Encoding.Latin1.GetBytes(sequence.ToString())[0];
    byte endtx = partition > 0 ? ETB : ETX;

    if (simulationMode && new Random().Next(0, 2) == 0)
    {
      currentSequence = 9; // salah sequence
      Console.WriteLine("Simulation: wrong sequence");
    }

    if (partition > 0)
    {
      packet = new List<byte> { STX, currentSequence };
      packet.AddRange(content);
      packet.Add(endtx);
      checkSum = GenerateCheckSum(packet.ToArray(), endtx);
    }
    else
    {
      packet = new List<byte> { STX, currentSequence };
      packet.AddRange(content);
      packet.Add(CR);
      packet.Add(endtx);
      checkSum = GenerateCheckSum(packet.ToArray(), endtx);
    }

    if (simulationMode && new Random().Next(0, 2) == 0)
    {
      checkSum = "FF"; // salah checksum
      Console.WriteLine("Simulation: wrong checksum");
    }

    byte[] checkSumBytes = Encoding.Latin1.GetBytes(checkSum);
    packet.AddRange(checkSumBytes);
    packet.Add(CR);
    packet.Add(LF);

    Log.Information("Validating checksum before sending: " + IsValidChecksum(packet.ToArray(), endtx));

    stream.Write(packet.ToArray());
    LogPacket(packet.ToArray());
    Console.WriteLine("Sent text to Server: " + text.Trim());

    sequence = sequence < MAX_SEQUENCE ? sequence + 1 : 0;
  }
}
