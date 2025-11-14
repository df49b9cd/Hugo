using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Hugo.Tests;

public class DeterministicJsonContextTests
{
    [Fact(Timeout = 15_000)]
    public void Default_ShouldExposeEffectEnvelopeMetadata()
    {
        var context = DeterministicJsonSerialization.DefaultContext;
        var typeInfo = context.GetTypeInfo(typeof(DeterministicEffectStore.EffectEnvelope));

        Assert.NotNull(typeInfo);
    }

    [Fact(Timeout = 15_000)]
    public void Default_ShouldRoundTripErrorUsingMetadata()
    {
        var context = DeterministicJsonSerialization.DefaultContext;
        var errorTypeInfo = Assert.IsType<JsonTypeInfo<Error>>(context.GetTypeInfo(typeof(Error)));
        var error = Error.From("boom").WithMetadata("stage", "beta");

        var payload = JsonSerializer.SerializeToUtf8Bytes(error, errorTypeInfo);
        var deserialized = JsonSerializer.Deserialize(payload, errorTypeInfo);

        Assert.NotNull(deserialized);
        Assert.Equal("boom", deserialized!.Message);
        Assert.Equal("beta", deserialized.Metadata["stage"]);
    }
}
