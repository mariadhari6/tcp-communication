using System;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Collections.Generic;
using Serilog;

public class Program
{
  private static readonly byte ENQ = 5;
  private static readonly byte ACK = 6;
  private static readonly byte STX = 2;
  private static readonly byte ETX = 3;
  private static readonly byte CR = 13;
  private static readonly byte EOT = 4;
  private static readonly byte ETB = 23;
  private static readonly byte LF = 10;
  private static readonly byte NAK = 21;
  private static readonly int MAX_SEQUENCE = 7;
  private static int nextSequence = 1;

  private static readonly List<byte> packetBuffer = new();
  private static string currentMessage = "";
  private static readonly List<string> messageCollections = new();

  private static TcpListener? listener;

  static void Main(string[] args)
  {
    Log.Logger = new LoggerConfiguration()
      .MinimumLevel.Information()
      .WriteTo.Console()
      .WriteTo.File("logs/myapp-.txt", rollingInterval: RollingInterval.Day)
      .CreateLogger();

    int port = int.Parse(Environment.GetEnvironmentVariable("TCP_PORT") ?? "5000");
    listener = new TcpListener(IPAddress.Any, port);
    listener.Start();
    Console.WriteLine($"TCP Server started on port {port}");

    while (true)
    {
      var client = listener.AcceptTcpClient();
      Console.WriteLine("Client connected.");
      var stream = client.GetStream();

      // handle client async/threaded
      Thread t = new(() => HandleClient(client, stream));
      t.Start();
    }
  }

  private static void Reset()
  {
    nextSequence = 1;
    currentMessage = "";
    messageCollections.Clear();
    packetBuffer.Clear();
  }

  private static void LogPacket(byte[] packet)
  {
    string packetString = Encoding.Latin1.GetString(packet);
    Log.Information("Packet String: " + packetString
      .Replace("\r", "<CR>")
      .Replace("\n", "<LF>")
      .Replace(((char)STX).ToString(), "<STX>")
      .Replace(((char)ETX).ToString(), "<ETX>")
      .Replace(((char)ETB).ToString(), "<ETB>"));
  }

  private static string ExtractText(byte[] packet, bool tailed = false)
  {
    if (packet[0] != STX)
      throw new ArgumentException("Packet must start with STX");

    if (tailed && (packet.Length < 8 || packet[^5] != ETB))
      throw new ArgumentException("Invalid packet format (tailed)");
    if (!tailed && (packet.Length < 9 || packet[^5] != ETX))
      throw new ArgumentException("Invalid packet format");

    if (!tailed)
    {
      int crIndex = Array.IndexOf(packet, CR, 1);
      if (crIndex == -1 || crIndex != packet.Length - 6)
        throw new ArgumentException("Invalid packet format (tailed)");

      int textLength = crIndex - 1;
      int startIndex = 2;
      List<byte> textBytes = new(packet[startIndex..(1 + textLength)]);
      return Encoding.Latin1.GetString(textBytes.ToArray());
    }
    else
    {
      int etbIndex = Array.IndexOf(packet, ETB, 1);
      int textLength = etbIndex - 1;
      int startIndex = 2;
      List<byte> textBytes = new(packet[startIndex..(1 + textLength)]);
      return Encoding.Latin1.GetString(textBytes.ToArray());
    }
  }

  private static bool IsValidSequence(byte sequence)
  {
    return Encoding.Latin1.GetString(new[] { sequence }) == nextSequence.ToString();
  }

  private static string GenerateCheckSum(byte[] packet, byte endtx)
  {
    int indexSTX = Array.IndexOf(packet, STX);
    int indexENDTX = Array.LastIndexOf(packet, endtx);

    if (indexSTX == -1 || indexENDTX == -1 || indexENDTX <= indexSTX)
      throw new ArgumentException("Invalid packet format");

    byte[] contentBytes = packet[(indexSTX + 1)..(indexENDTX + 1)];
    string contentString = Encoding.Latin1.GetString(contentBytes);
    Log.Information("Index ENDTX: " + indexENDTX);
    Log.Information("Content String: " + contentString);
    Log.Information("Content Length: " + contentString.Length);

    int total = 0;
    foreach (var c in contentBytes) total += c;
    return (total % 256).ToString("X2");
  }

  private static bool IsValidChecksum(byte[] packet, byte endtx)
  {
    if (packet[^2] != CR || packet[^1] != LF)
      return false;

    int indexENDTX = Array.IndexOf(packet, endtx);
    List<byte> checkSumBytes = new(packet[(indexENDTX + 1)..(indexENDTX + 3)]);
    string receivedCheckSum = Encoding.Latin1.GetString(checkSumBytes.ToArray());
    string calculatedCheckSum = GenerateCheckSum(packet, endtx);

    Log.Information("===Checksum Validation===");
    Log.Information("Calculated Checksum: " + calculatedCheckSum);
    Log.Information("Received Checksum: " + receivedCheckSum);

    return receivedCheckSum.Equals(calculatedCheckSum, StringComparison.OrdinalIgnoreCase);
  }

  private static void HandleClient(TcpClient client, NetworkStream stream)
  {
    try
    {
      byte[] buffer = new byte[1024];
      int bytesRead;

      while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
      {
        byte[] incoming = new byte[bytesRead];
        Array.Copy(buffer, incoming, bytesRead);

        foreach (byte b in incoming)
        {
          if (b == ENQ && incoming.Length == 1)
          {
            Console.WriteLine("Received ENQ from Client.");
            stream.Write(new byte[] { ACK });
            Console.WriteLine("Send ACK to Client.");
            packetBuffer.Clear();
          }
          else if (b == EOT && incoming.Length == 1)
          {
            Console.WriteLine("Received EOT from Client.");
            Console.WriteLine("Transmission ended.");
            string allMessages = string.Join("\n", messageCollections);
            Console.WriteLine("All Messages:\n" + allMessages);
            Reset();
          }
          else
          {
            packetBuffer.Add(b);

            if (b == LF) // paket selesai
            {
              try
              {
                LogPacket(packetBuffer.ToArray());
                byte endTx = packetBuffer[^5];
                if (endTx != ETX && endTx != ETB)
                {
                  Console.WriteLine("Invalid packet format (missing ETX/ETB)");
                  packetBuffer.Clear();
                  stream.Write(new byte[] { NAK });
                  continue;
                }

                if (!IsValidSequence(packetBuffer[1]))
                {
                  Console.WriteLine("Invalid sequence number. Expected: " + nextSequence);
                  packetBuffer.Clear();
                  stream.Write(new byte[] { NAK });
                  continue;
                }

                if (!IsValidChecksum(packetBuffer.ToArray(), endTx))
                {
                  Console.WriteLine("Invalid checksum.");
                  packetBuffer.Clear();
                  stream.Write(new byte[] { NAK });
                  continue;
                }

                string text = ExtractText(packetBuffer.ToArray(), tailed: endTx == ETB);
                if (endTx == ETB)
                {
                  Console.WriteLine("Data Received (Tailed): " + text);
                  currentMessage += text;
                  packetBuffer.Clear();
                  stream.Write(new byte[] { ACK });
                  nextSequence = (nextSequence + 1) % (MAX_SEQUENCE + 1);
                }
                else if (endTx == ETX)
                {
                  text = currentMessage + text;
                  messageCollections.Add(text);
                  Console.WriteLine("Data Received: " + text);
                  currentMessage = "";
                  packetBuffer.Clear();
                  stream.Write(new byte[] { ACK });
                  nextSequence = (nextSequence + 1) % (MAX_SEQUENCE + 1);
                }
              }
              catch (Exception ex)
              {
                Console.WriteLine("Packet error: " + ex.Message);
                packetBuffer.Clear();
                stream.Write(new byte[] { NAK });
              }
            }
          }
        }
      }
    }
    catch (Exception ex)
    {
      Console.WriteLine("Client error: " + ex.Message);
    }
    finally
    {
      client.Close();
      Console.WriteLine("Client disconnected.");
    }
  }
}
