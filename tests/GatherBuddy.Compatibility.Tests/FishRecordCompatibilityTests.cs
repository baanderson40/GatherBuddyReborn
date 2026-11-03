using System.Reflection;
using GatherBuddy.FishTimer;
using MessagePack;
using Xunit;

namespace GatherBuddy.Compatibility.Tests;

public sealed class FishRecordCompatibilityTests
{
    // Captured from actual FishRecord generated formatter, MessagePack 3.1.3, commit 62085c3.
    // Includes V2 file header and every numeric key/private field. Never regenerate during tests.
    private const string BaselineV2 = "ApHcABLNMaDNEzzS//4dwM5lU/EAAM0JxM0LuM0MHM0BpCpCAso/oAAAysAgAADKQHAAAMo/AAAA2SQwMDExMjIzMy00NDU1LTY2NzctODg5OS1hYWJiY2NkZGVlZmZK";
    private static FishRecord CreateRecord()
    {
        var record = new FishRecord
        {
            Id = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
            ContentIdHash = -123456,
            Bite = 2500,
            Perception = 3000,
            Gathering = 3100,
            Size = 420,
            Amount = 2,
        };
        Set(record, "_bait", 12704u);
        Set(record, "_catch", 4924u);
        Set(record, "_timeStamp", 1700000000);
        Set(record, "_fishingSpot", (ushort)42);
        Set(record, "_tugAndHook", (byte)0x42);
        Set(record, "_x", 1.25f);
        Set(record, "_y", -2.5f);
        Set(record, "_z", 3.75f);
        Set(record, "_rotation", 0.5f);
        Set(record, "_worldId", 74u);
        return record;
    }

    private static void Set(FishRecord record, string name, object value)
        => typeof(FishRecord).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(record, value);

    [Fact]
    public void OldFormatterFixtureDecodesEveryPersistedField()
    {
        var decoded = Assert.Single(FishRecordSerialization.Read(Convert.FromBase64String(BaselineV2)[1..]));
        AssertFieldsEqual(CreateRecord(), decoded);
    }

    private static void AssertFieldsEqual(FishRecord expected, FishRecord actual)
    {
        foreach (var field in typeof(FishRecord).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            Assert.Equal(field.GetValue(expected), field.GetValue(actual));
    }

    [Fact]
    public void NewWriterProducesIdenticalOldReaderCompatibleBytes()
    {
        using var stream = new MemoryStream();
        stream.WriteByte(FishRecord.Version);
        FishRecordSerialization.Write(stream, [CreateRecord()]);
        Assert.Equal(Convert.FromBase64String(BaselineV2), stream.ToArray());
    }

    [Fact]
    public void MultipleRecordsAndEmptyListRoundTrip()
    {
        using var stream = new MemoryStream();
        FishRecordSerialization.Write(stream, [CreateRecord(), CreateRecord()]);
        var records = FishRecordSerialization.Read(stream.ToArray());
        Assert.Equal(2, records.Count);
        Assert.All(records, record => AssertFieldsEqual(CreateRecord(), record));
        Assert.Empty(FishRecordSerialization.Read([0x90]));
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0xc1 })]
    [InlineData(new byte[] { 0xc0 })]
    [InlineData(new byte[] { 0x91, 0xc0 })]
    [InlineData(new byte[] { 0x90, 0x90 })]
    public void InvalidPayloadRejected(byte[] payload)
        => Assert.Throws<MessagePackSerializationException>(() => FishRecordSerialization.Read(payload));

    [Fact]
    public void TruncatedRecordRejected()
        => Assert.Throws<MessagePackSerializationException>(() => FishRecordSerialization.Read(Convert.FromBase64String(BaselineV2)[1..^1]));

    [Fact]
    public void DeeplyNestedUnknownFieldSkippedWithoutRecursiveMaterialization()
    {
        var payload = Convert.FromBase64String(BaselineV2)[1..];
        payload[3] = 19; // Add one forward-compatible unknown field after the 18 existing keys.
        var deep = payload.Concat(Enumerable.Repeat((byte)0x91, 550)).Append((byte)0xc0).ToArray();
        // Patched MessagePack Skip is iterative, not recursive. Unknown future fields need not be materialized.
        AssertFieldsEqual(CreateRecord(), Assert.Single(FishRecordSerialization.Read(deep)));
    }

    [Fact]
    public void SerializerDoesNotChangeProcessGlobalOptions()
    {
        var options = MessagePackSerializer.DefaultOptions;
        FishRecordSerialization.Read([0x90]);
        Assert.Same(options, MessagePackSerializer.DefaultOptions);
    }
}
