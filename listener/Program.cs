using System;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Collections.Generic;
using Serilog;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using Sprache;

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

  private static JArray? HeaderStructure;
  private static JArray? PatientStructure;
  private static JArray? OrderStructure;
  private static JArray? ResultStructure;
  private static JArray? TerminationStructure;
  private static JArray? QueryStructure;
  private static JArray? OrderList;

  private static readonly List<byte> packetBuffer = new();
  private static string currentMessage = "";
  private static readonly List<string> messageCollections = new();

  private static TcpListener? listener;

  private static string TEXT = ""; // pesan yang akan dikirim pada client setelah menerima query
  private static int line = 0;
  private static int partition = 0;
  private static int lastPartition = partition;
  private static int sequence = 1;
  private static readonly int maxCharactersPerLine = 50;


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

  private static string ConvertJSONToText(JObject data)
  {
    List<string> lines = new();

    // Header
    if (data["Header"] is JObject header)
    {
      lines.Add(GetHeaderText(HeaderStructure!, header));
    }
    // Query
    if (data["Query"] is JObject query)
    {
      lines.Add(GetContentText(QueryStructure!, query, sequence: "1"));
    }
    // Patient
    if (data["Patient"] is JObject patient)
    {
      lines.Add(GetContentText(PatientStructure!, patient, sequence: "1"));
    }
    // Order
    if (data["Order"] is JObject order)
    {
      lines.Add(GetContentText(OrderStructure!, order));
    }

    // Results (can be multiple)
    if (data["Result"] is JArray results)
    {
      for (int i = 0; i < results.Count; i++)
      {
        if (results[i] is JObject result)
        {
          lines.Add(GetContentText(ResultStructure!, result, sequence: (i + 1).ToString()));
        }
      }
    }

    // Termination
    if (data["Termination"] is JObject termination)
    {
      lines.Add(GetContentText(TerminationStructure!, termination, sequence: "1"));
    }
    return string.Join("\n", lines);
  }
  static void Main(string[] args)
  {
    try
    {
      Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Information()
        .WriteTo.Console()
        .WriteTo.File("logs/myapp-.txt", rollingInterval: RollingInterval.Day)
        .CreateLogger();

      // Load structure

      Log.Information("Load Structure From JSON File...");
      string structureString = File.ReadAllText("structure.json");
      dynamic? structure = null;

      if (!string.IsNullOrEmpty(structureString))
      {
        structure = JsonConvert.DeserializeObject<dynamic>(structureString);
      }

      if (structure == null)
      {
        Log.Error("Failed to load structure file...");
        return;
      }

      HeaderStructure = (JArray)structure["Header"]["fields"];
      QueryStructure = (JArray)structure["Query"]["fields"];
      PatientStructure = (JArray)structure["Patient"]["fields"];
      OrderStructure = (JArray)structure["Order"]["fields"];
      ResultStructure = (JArray)structure["Result"]["fields"];
      TerminationStructure = (JArray)structure["Termination"]["fields"];

      // Load data orders
      Log.Information("Load Data Order from orders.json...");
      string orderString = File.ReadAllText("orders.json");
      OrderList = new JArray(JsonConvert.DeserializeObject<dynamic>(orderString));


      if (HeaderStructure == null || PatientStructure == null || OrderStructure == null || ResultStructure == null || TerminationStructure == null)
      {
        Log.Error("Failed to Parse Structure");
        return;
      }

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
    catch (Exception err)
    {
      Log.Error("Error message: " + err.Message);
      throw;
    }
    finally
    {
      Log.Information("Program exit(0)");
    }
  }

  private static void Reset()
  {
    TEXT = "";
    partition = 0;
    lastPartition = partition;
    sequence = 1;
    line = 0;

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
  private static JObject ClearJObjectData(JObject data)
  {
    // copy daftar property biar aman dari "collection modified" error
    var keys = data.Properties().ToList();

    foreach (var prop in keys)
    {
      string keyName = prop.Name;

      // hapus jika key bernama [NONE]
      if (keyName == "[NONE]")
      {
        data.Remove(keyName);
        continue;
      }

      // kalau value berupa JObject, rekursif
      if (prop.Value.Type == JTokenType.Object)
      {
        data[keyName] = ClearJObjectData((JObject)prop.Value);
      }
      // kalau value berupa array, cek setiap item apakah JObject
      else if (prop.Value.Type == JTokenType.Array)
      {
        JArray arr = (JArray)prop.Value;
        for (int i = 0; i < arr.Count; i++)
        {
          if (arr[i].Type == JTokenType.Object)
          {
            arr[i] = ClearJObjectData((JObject)arr[i]);
          }
        }
      }
    }

    return data;
  }



  private static void HandleQueryMessage(JObject queryData, NetworkStream stream)
  {
    /**
      1. Find order data from file order.json
      2. 
        2.1 if found convert to text
        2.2 if not found re-convert queryData to string as order messsage (except "Universal Test ID" attribute)
    */
    Log.Information("Handling Query Message...");
    string queryString = queryData!["Query"]!["Starting Range ID No."]!.ToString();
    if (string.IsNullOrEmpty(queryString))
    {
      Log.Warning("Incomplete Query Parameters");
      return;
    }
    Log.Information("Finding Orders...");
    Log.Information("Query String: " + queryString);
    if (OrderList!.Where(v => v["Order"]!["Specimen ID"]!.ToString().Equals(queryString, StringComparison.OrdinalIgnoreCase)).FirstOrDefault() is JObject item)
    {
      TEXT = ConvertJSONToText(item);
      Log.Information("Outcoming Order Message: \n" + TEXT);
      stream.Write([ENQ]);
      Log.Information("Send ENQ to client...");
      return;
    }

    JObject order = new JObject
    {
      {"Specimen ID", queryData!["Query"]!["Starting Range ID No."]},
      {"Priority", "R"},
      {"Requested/order date and time", "20250924001956"}
    };
    JObject response = new JObject
    {
      {"Header", queryData["Header"]},
      {"Patient", new JObject{}},
      {"Order", order},
      {"Termination", queryData["Termination"]}
    };

    Log.Information("Order Not Found...");
    TEXT = ConvertJSONToText(response);
    Log.Information("Outcoming Order Not Found Message: \n" + TEXT);
    stream.Write([ENQ]);
    Log.Information("Send ENQ to Client...");
  }

  private static void OnTransmitionEnd(string allMessages, NetworkStream stream)
  {
    List<string> lines = [.. allMessages.Split("\n")];
    List<string> header = [.. lines.Where(s => s[0].ToString() == "H")];
    List<string> patients = [.. lines.Where(s => s[0].ToString() == "P")];
    List<string> orders = [.. lines.Where(s => s[0].ToString() == "O")];
    List<string> results = [.. lines.Where(s => s[0].ToString() == "R")];
    List<string> termination = [.. lines.Where(s => s[0].ToString() == "L")];
    List<string> queries = [.. lines.Where(s => s[0].ToString() == "Q")];

    var reconstructedHeader = ParseResultText(HeaderStructure!, header);
    var reconstructedTermination = ParseResultText(TerminationStructure!, termination);

    JObject r = new JObject();
    if (queries.Count > 0)
    {
      var reconstructedQuery = ParseResultText(QueryStructure!, queries);
      r = new JObject
      {
        {"Header", reconstructedHeader[0]},
        {"Query", reconstructedQuery[0]},
        {"Termination", reconstructedTermination[0]}
      };
      JObject queryData = ClearJObjectData(r);
      Log.Information(queryData.ToString(Formatting.Indented));
      HandleQueryMessage(queryData, stream);
      return;
    }

    // Handle result message
    // if the message type is result, trigger to client
    if (patients.Count > 0 && orders.Count > 0 && results.Count > 0)
    {
      var reconstructedPatients = ParseResultText(PatientStructure!, patients);
      var reconstructedOrders = ParseResultText(OrderStructure!, orders);
      var reconstructedResults = ParseResultText(ResultStructure!, results);
      r = new JObject
      {
        {"Header", reconstructedHeader[0]},
        {"Patient", reconstructedPatients[0]},
        {"Order", reconstructedOrders[0]},
        {"Result", reconstructedResults},
        {"Termination", reconstructedTermination}
      };
      // Log.Information(r.ToString(Formatting.Indented));
      JObject resultData = ClearJObjectData(r);
      Log.Information(resultData.ToString(Formatting.Indented));
      return;
    }
  }

  private static void SendText(NetworkStream stream)
  {
    string? text = GetText();
    Console.WriteLine($"Partition: {partition}, Sequence: {sequence}");

    if (text == null)
    {
      Console.WriteLine("All text sent.");
      stream.Write(new byte[] { EOT });
      Console.WriteLine("Send EOT to Server.");
      Reset();
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
    Log.Information("Sent text to Client: " + text.Trim());

    sequence = sequence < MAX_SEQUENCE ? sequence + 1 : 0;
  }

  private static void HandleClient(TcpClient client, NetworkStream stream)
  {
    try
    {
      byte[] buffer = new byte[1024];
      int bytesRead;

      while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
      {
        byte data = buffer[0];
        if (data == ACK)
        {
          Log.Information("Receive ACK from Client.");
          SendText(stream);
        }
        else if (data == NAK)
        {
          Log.Information("Receive NAK from client...");
          sequence = sequence == 0 ? MAX_SEQUENCE : sequence - 1;
          if (partition == 0 && line > 0)
            line = Math.Max(0, line - 1);
          partition = lastPartition;
          SendText(stream);
        }
        else
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
              Reset();
              // Console.WriteLine("All Messages:\n" + allMessages);
              Log.Information("All Messages:\n" + allMessages);
              OnTransmitionEnd(allMessages, stream);
              // stream.Write([ENQ]);
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
}
