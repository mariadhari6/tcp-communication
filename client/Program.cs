using System;
using System.Text;
using System.Net.Sockets;
using DotNetEnv;
using Serilog;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Text.Json.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public class Program
{
  private static string TEXT = """
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

  private static TcpClient? client;
  private static NetworkStream? stream;

  private static JArray? HeaderStructure;
  private static JArray? PatientStructure;
  private static JArray? OrderStructure;
  private static JArray? ResultStructure;
  private static JArray? TerminationStructure;
  private static JArray? QueryStructure;

  private static JObject? DATA_RESULT; // sent example result for mechanism transfer & receive data

  private static int indexQuery = 0;
  private static JArray? QueryList;
  private static List<dynamic> OrderList = [];
  private static JObject? STRUCT;
  private static readonly List<byte> packetBuffer = new();
  private static string currentMessage = ""; // incoming message from server
  private static int nextSequence = 1; // for incoming message from server
  private static readonly List<string> messageCollections = new(); // for incoming message from server

  public static void Main(string[] args)
  {
    Log.Logger = new LoggerConfiguration()
      .MinimumLevel.Information()
      .WriteTo.Console()
      .WriteTo.File("logs/myapp-client-.txt", rollingInterval: RollingInterval.Day)
      .CreateLogger();

    try
    {
      Env.Load();
      Log.Information("Load .env file");
      string host = Environment.GetEnvironmentVariable("TCP_HOST") ?? "127.0.0.1";
      int port = int.Parse(Environment.GetEnvironmentVariable("TCP_PORT") ?? "5000");


      string structureString = File.ReadAllText("structure.json");
      dynamic? structure = null;

      string dataString = File.ReadAllText("data.json");
      dynamic? data = null;

      string queryString = File.ReadAllText("query.json");
      dynamic? query = null;

      if (!string.IsNullOrEmpty(structureString))
      {
        structure = JsonConvert.DeserializeObject<dynamic>(structureString);
      }
      if (!string.IsNullOrEmpty(dataString))
      {
        data = JsonConvert.DeserializeObject<dynamic>(dataString);
      }
      if (!string.IsNullOrEmpty(queryString))
      {
        query = JsonConvert.DeserializeObject<dynamic>(queryString);
      }
      if (data == null || structure == null || query == null)
      {
        Log.Error("Failed to load structure, data or query");
        return;
      }

      QueryList = (JArray)query!;
      STRUCT = (JObject)structure!;
      DATA_RESULT = data![0];

      TEXT = ConvertJSONToText(STRUCT, DATA_RESULT);
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

  private static string GetContentText(JArray structure, dynamic data, string sep = "|", string sequence = "1")
  {

    var listText = new List<string>();

    foreach (var item in structure)
    {
      JObject itemObj = (JObject)item;
      List<string> keys = itemObj.Properties().Select(p => p.Name).ToList();

      // we only use the first key in this structure
      string key = keys[0];
      JToken tokenNode = itemObj[key]!;

      string token = "";

      if (tokenNode.Type == JTokenType.String ||
          tokenNode.Type == JTokenType.Integer ||
          tokenNode.Type == JTokenType.Boolean ||
          tokenNode.Type == JTokenType.Float)
      {
        // JValue
        token = tokenNode?.ToString() ?? "";
        if (string.IsNullOrEmpty(token))
        {
          // fallback ke data
          token = data[key]?.ToString() ?? "";
        }
      }
      else if (tokenNode.Type == JTokenType.Array)
      {
        // recursive call for nested structure
        var structureChild = (JArray)itemObj[key]!;
        var dataChild = data[key];
        JToken tokenChild = dataChild;

        if (dataChild != null)
        {
          if (tokenChild.Type == JTokenType.Array)
          {
            List<string> tokenList = new();
            for (int i = 0; i < ((JArray)dataChild).Count; i++)
            {
              tokenList.Add(GetContentText(structureChild, dataChild[i], sep: "^", sequence: sequence));
            }
            token = string.Join('\\', tokenList);
          }
          else
          {
            token = GetContentText(structureChild, dataChild, sep: "^", sequence: sequence);
          }
        }
      }
      else if (tokenNode.Type == JTokenType.Object)
      {
        // nested JObject
        token = GetContentText(new JArray(tokenNode), data[key], sep: "^", sequence: sequence);
      }
      if (key == "Sequence No.")
      {
        token = sequence;
      }
      listText.Add(token);
    }

    return string.Join(sep, listText);
  }
  private static string GetHeaderText(JArray structure, dynamic data, string sep = "|")
  {
    var listText = new List<string>();

    foreach (var item in structure)
    {
      JObject itemObj = (JObject)item;
      List<string> keys = itemObj.Properties().Select(p => p.Name).ToList();

      // we only use the first key in this structure
      string key = keys[0];
      JToken tokenNode = itemObj[key];

      string token = "";

      if (tokenNode.Type == JTokenType.String ||
          tokenNode.Type == JTokenType.Integer ||
          tokenNode.Type == JTokenType.Boolean ||
          tokenNode.Type == JTokenType.Float)
      {
        // JValue
        token = tokenNode?.ToString() ?? "";
        if (string.IsNullOrEmpty(token))
        {
          // fallback ke data
          token = data[key]?.ToString() ?? "";
        }
      }
      else if (tokenNode.Type == JTokenType.Array)
      {
        // recursive call for nested structure
        var structureChild = (JArray)itemObj[key];
        var dataChild = data[key];
        JToken tokenChild = dataChild;

        if (dataChild != null)
        {
          if (tokenChild.Type == JTokenType.Array)
          {
            List<string> tokenList = new();
            for (int i = 0; i < ((JArray)dataChild).Count; i++)
            {
              tokenList.Add(GetHeaderText(structureChild, dataChild, "^"));
            }
            token = string.Join('\\', tokenList);
          }
          else
          {
            token = GetHeaderText(structureChild, dataChild, "^");
          }
        }
      }
      else if (tokenNode.Type == JTokenType.Object)
      {
        // nested JObject (rare case in your sample)
        token = GetHeaderText(new JArray(tokenNode), data[key], sep: "^");
      }

      listText.Add(token);
    }

    return string.Join(sep, listText);
  }
  private static JObject ParseContentText(JArray structure, string line, string sep = "|")
  {
    var obj = new JObject();
    string[] tokens = line.Split(sep);

    int index = 0;
    foreach (var field in structure)
    {
      JObject fieldObj = (JObject)field;
      string key = fieldObj.Properties().First().Name;
      JToken fieldDef = fieldObj[key];

      if (fieldDef.Type == JTokenType.String) // simple field
      {
        obj[key] = index < tokens.Length ? tokens[index] : "";
        index++;
      }
      else if (fieldDef.Type == JTokenType.Array) // nested array (like Universal Test ID)
      {
        string childLine = index < tokens.Length ? tokens[index] : "";
        index++;
        List<string> listLine = childLine.Split('\\').ToList();
        JArray childStructure = (JArray)fieldDef;
        if (listLine.Count > 1)
        {
          JArray childArray = new JArray();
          for (int i = 0; i < listLine.Count; i++)
          {
            JObject childObj = ParseContentText(childStructure, listLine[i], "^");
            childArray.Add(childObj);
          }
          obj[key] = childArray;
        }
        else
        {
          JObject childObj = ParseContentText(childStructure, childLine, "^");
          obj[key] = childObj;
        }
      }
      else if (fieldDef.Type == JTokenType.Object)
      {
        // nested object
        string childLine = index < tokens.Length ? tokens[index] : "";
        index++;

        JObject childObj = ParseContentText(new JArray(fieldDef), childLine, "^");
        obj[key] = childObj;
      }
    }

    return obj;
  }
  private static JArray ParseResultText(JArray structure, List<string> lines, string sep = "|")
  {
    var arr = new JArray();
    foreach (var line in lines)
    {
      arr.Add(ParseContentText(structure, line, sep));
    }
    return arr;
  }
  private static string ConvertJSONToText(JObject structure, JObject data)
  {
    var lines = new List<string>();

    // Header
    if (structure["Header"]?["fields"] is JArray headerStructure &&
        data["Header"] is JObject dataHeader)
    {
      lines.Add(GetHeaderText(headerStructure, dataHeader));
    }

    // Query
    if (structure["Query"]?["fields"] is JArray queryStructure &&
        data["Query"] is JObject dataQuery)
    {
      lines.Add(GetContentText(queryStructure, dataQuery, sequence: "1"));
    }

    // Patient
    if (structure["Patient"]?["fields"] is JArray patientStructure &&
        data["Patient"] is JObject dataPatient)
    {
      lines.Add(GetContentText(patientStructure, dataPatient, sequence: "1"));
    }

    // Order
    if (structure["Order"]?["fields"] is JArray orderStructure &&
        data["Order"] is JObject dataOrder)
    {
      lines.Add(GetContentText(orderStructure, dataOrder, sequence: "1"));
    }

    // Result (can be multiple)
    if (structure["Result"]?["fields"] is JArray resultStructure &&
        data["Result"] is JArray resultList)
    {
      for (int i = 0; i < resultList.Count; i++)
      {
        if (resultList[i] is JObject resultItem)
        {
          lines.Add(GetContentText(resultStructure, resultItem, sequence: (i + 1).ToString()));
        }
      }
    }

    // Termination
    if (structure["Termination"]?["fields"] is JArray terminationStructure &&
        data["Termination"] is JObject dataTermination)
    {
      lines.Add(GetContentText(terminationStructure, dataTermination, sequence: "1"));
    }

    return string.Join("\n", lines);
  }


  private static void Reset()
  {
    TEXT = "";
    partition = 0;
    lastPartition = partition;
    sequence = 1;
    line = 0;

    packetBuffer.Clear();
    nextSequence = 1;
    messageCollections.Clear();
    currentMessage = "";
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

  private static void HandleTransmitionEnd(string allMessages, NetworkStream stream)
  {
    // List<string> lines = [.. allMessages.Split("\n")];
    // List<string> header = [.. lines.Where(s => s[0].ToString() == "H")];
    // List<string> patients = [.. lines.Where(s => s[0].ToString() == "P")];
    // List<string> orders = [.. lines.Where(s => s[0].ToString() == "O")];
    // List<string> termination = [.. lines.Where(s => s[0].ToString() == "L")];

    // JArray reconstructedHeader = ParseResultText(HeaderStructure!, header);
    // Log.Information("RECONSTRUCTED HEADER SUCCESS");
    // JArray reconstructedTermination = ParseResultText(TerminationStructure!, termination);
    // Log.Information("RECONSTRUCTED TERMINATION SUCCESS");
    // JArray reconstructedPatients = ParseResultText(PatientStructure!, patients);
    // Log.Information("RECONSTRUCTED PATIENTS SUCCESS");
    // JArray reconstructedOrders = ParseResultText(OrderStructure!, orders);
    // Log.Information("RECONSTRUCTED ORDERs SUCCESS");


    // JObject newOrder = new JObject
    // {
    //   {"Header", reconstructedHeader[0]},
    //   {"Patient", reconstructedPatients[0]},
    //   {"Order", reconstructedOrders[0]},
    //   {"Termination", reconstructedTermination[0]}
    // };
    OrderList.Add(new JObject());
    TEXT = ConvertJSONToText(STRUCT!, DATA_RESULT!);
    // Send ENQ to start communication
    stream.Write([ENQ]);
  }

  private static void ReceiveOrderHandler(byte[] incoming)
  {
    foreach (byte b in incoming)
    {
      if (b == ENQ && incoming.Length == 1)
      {
        Log.Information("Receive ENQ from Server.");
        stream.Write([ACK]);
        Log.Information("Send ACK to Server.");
        packetBuffer.Clear();

      }
      else if (b == EOT && incoming.Length == 1)
      {
        Log.Information("Receive EOT from Server.");
        Log.Information("Transmition Ended");
        string allMessages = string.Join("\n", messageCollections);
        Reset();
        Log.Information("Message Order: \n" + allMessages);
        HandleTransmitionEnd(allMessages, stream!);
      }
      else
      {
        packetBuffer.Add(b);
        if (b == LF) // packet selesai
        {
          try
          {
            LogPacket([.. packetBuffer]);
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
            Log.Error("Packet error: " + ex.Message);
            packetBuffer.Clear();
            stream.Write([NAK]);
          }
        }
      }
    }
  }

  private static void ReceiveHandler()
  {
    byte[] buffer = new byte[1024];
    int bytesRead;
    try
    {
      while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
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
          // ReceiveHandler();
          byte[] incoming = new byte[bytesRead];
          Array.Copy(buffer, incoming, bytesRead);
          ReceiveOrderHandler(incoming);
        }
      }
    }
    catch (Exception ex)
    {
      Console.WriteLine("Receive error: " + ex.Message);
    }
  }

  private static void HandleNextQuery()
  {
    TEXT = ConvertJSONToText(STRUCT!, (JObject)QueryList![indexQuery]) ?? "";
    if (!string.IsNullOrEmpty(TEXT))
    {
      Log.Information("Handle NEXT QUEURY");
      stream.Write([ENQ]);
      indexQuery++;
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

      Thread.Sleep(3000);

      if (indexQuery < QueryList.Count && OrderList.Count == indexQuery)
      {
        HandleNextQuery();
      }
      // running = false;
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
      currentSequence = Encoding.Latin1.GetBytes("9")[0]; // salah sequence
      Console.WriteLine("Simulation: wrong sequence");
    }

    if (partition > 0)
    {
      packet = [STX, currentSequence, .. content, endtx];
      checkSum = GenerateCheckSum(packet.ToArray(), endtx);
    }
    else
    {
      packet = [STX, currentSequence, .. content, CR, endtx];
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
