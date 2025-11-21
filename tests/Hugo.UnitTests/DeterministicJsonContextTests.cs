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

        typeInfo.ShouldNotBeNull();
    }

    [Fact(Timeout = 15_000)]
    public void Default_ShouldRoundTripErrorUsingMetadata()
    {
        var context = DeterministicJsonSerialization.DefaultContext;
        var errorTypeInfo = context.GetTypeInfo(typeof(Error)).ShouldBeOfType<JsonTypeInfo<Error>>();
        var error = Error.From("boom").WithMetadata("stage", "beta");

        var payload = JsonSerializer.SerializeToUtf8Bytes(error, errorTypeInfo);
        var deserialized = JsonSerializer.Deserialize(payload, errorTypeInfo);

        deserialized.ShouldNotBeNull();
        deserialized!.Message.ShouldBe("boom");
        deserialized.Metadata["stage"].ShouldBe("beta");
    }
}
