using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace Photobox.Bridge.WorkerIpc;

internal static class IpcJson
{
    public static byte[] Serialize<T>(T obj)
    {
        using var ms = new MemoryStream();
        var ser = new DataContractJsonSerializer(typeof(T));
        ser.WriteObject(ms, obj);
        return ms.ToArray();
    }

    public static T Deserialize<T>(byte[] jsonBytes)
    {
        using var ms = new MemoryStream(jsonBytes);
        var ser = new DataContractJsonSerializer(typeof(T));
        return (T)ser.ReadObject(ms);
    }

    public static string SerializeToString<T>(T obj)
    {
        var bytes = Serialize(obj);
        return Encoding.UTF8.GetString(bytes);
    }

    public static T DeserializeFromString<T>(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json ?? "");
        return Deserialize<T>(bytes);
    }
}
