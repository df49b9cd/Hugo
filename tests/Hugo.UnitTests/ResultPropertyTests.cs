using FsCheck;
using FsCheck.Fluent;

using static Hugo.Go;

namespace Hugo.Tests;

public class ResultPropertyTests
{
    [Fact(Timeout = 15_000)]
    public void Map_ComposesFunctionApplication() => Check.QuickThrowOnFailure(Prop.ForAll<int>(static value =>
                                                          {
                                                              static int Increment(int x) => x + 1;
                                                              static int Double(int x) => x * 2;

                                                              var sequential = Ok(value).Map(Increment).Map(Double);
                                                              var composed = Ok(value).Map(static x => Double(Increment(x)));

                                                              return sequential.Equals(composed);
                                                          }));

    [Fact(Timeout = 15_000)]
    public void Recover_ShouldNotRun_WhenResultIsSuccess() => Check.QuickThrowOnFailure(Prop.ForAll<int>(static value =>
                                                                   {
                                                                       var recovered = Ok(value).Recover(static _ => Result.Ok(-1));
                                                                       return recovered.Value == value;
                                                                   }));

    [Fact(Timeout = 15_000)]
    public void Ensure_ShouldFailForOddValues() => Check.QuickThrowOnFailure(Prop.ForAll<int>(static value =>
                                                        {
                                                            var ensured = Ok(value).Ensure(static v => v % 2 == 0, static v => Error.From($"{v} is odd", ErrorCodes.Validation));
                                                            return value % 2 == 0
                                                                ? ensured.IsSuccess && ensured.Value == value
                                                                : ensured is { IsFailure: true, Error.Code: ErrorCodes.Validation }
                                                                  && ensured.Error?.Message.Contains("odd", StringComparison.OrdinalIgnoreCase) == true;
                                                        }));

    [Fact(Timeout = 15_000)]
    public void Sequence_ShouldAccumulate_OnSuccess() => Check.QuickThrowOnFailure(Prop.ForAll<int[]>(static values =>
                                                              {
                                                                  var results = values.Select(static v => Result.Ok(v));
                                                                  var aggregated = Result.Sequence(results);

                                                                  return aggregated.IsSuccess && aggregated.Value.SequenceEqual(values);
                                                              }));

    [Fact(Timeout = 15_000)]
    public void Recover_ShouldRehydrateFailure() => Check.QuickThrowOnFailure(Prop.ForAll<NonEmptyString>(static message =>
                                                         {
                                                             var failure = Result.Fail<int>(Error.From(message.Item, ErrorCodes.Unspecified));

                                                             var recovered = failure.Recover(static error => Result.Ok(error.Message.Length));

                                                             return recovered.IsSuccess && recovered.Value == message.Item.Length;
                                                         }));
}
