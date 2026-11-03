using System.Collections.Generic;
using System.IO;
using MessagePack;

namespace GatherBuddy.FishTimer;

/// <summary>The existing V2 payload format, with explicit per-call untrusted-input protection.</summary>
public static class FishRecordSerialization
{
    private static readonly MessagePackSerializerOptions Options = MessagePackSerializerOptions.Standard
        .WithSecurity(MessagePackSecurity.UntrustedData);

    public static void Write(Stream stream, List<FishRecord> records)
        => MessagePackSerializer.Serialize(stream, records, Options);

    public static List<FishRecord> Read(byte[] payload)
    {
        var reader = new MessagePackReader(payload);
        var records = MessagePackSerializer.Deserialize<List<FishRecord>>(ref reader, Options);
        if (!reader.End)
            throw new MessagePackSerializationException("Unexpected data after fish-record payload.");
        if (records == null || records.Exists(static record => record == null))
            throw new MessagePackSerializationException("Fish-record payload must contain a non-null list of records.");
        return records;
    }
}
