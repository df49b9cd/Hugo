using FsCheck;
using FsCheck.Fluent;

using Hugo;

using static Hugo.Go;

namespace Hugo.Tests;

public class ResultPropertyTests
{
    [Fact]
    public void Map_ComposesFunctionApplication() => Check.QuickThrowOnFailure(Prop.ForAll<int>(value =>
                                                          {
                                                              static int Increment(int x) => x + 1;
                                                              static int Double(int x) => x * 2;

                                                              var sequential = Ok(value).Map(Increment).Map(Double);
                                                              var composed = Ok(value).Map(x => Double(Increment(x)));

                                                              return sequential.Equals(composed);
                                                          }));

    [Fact]
    public void Recover_ShouldNotRun_WhenResultIsSuccess() => Check.QuickThrowOnFailure(Prop.ForAll<int>(value =>
                                                                   {
                                                                       var recovered = Ok(value).Recover(_ => Result.Ok(-1));
                                                                       return recovered.Value == value;
                                                                   }));

    [Fact]
    public void Ensure_ShouldFailForOddValues() => Check.QuickThrowOnFailure(Prop.ForAll<int>(value =>
                                                        {
                                                            var ensured = Ok(value).Ensure(v => v % 2 == 0, v => Error.From($"{v} is odd", ErrorCodes.Validation));
                                                            return value % 2 == 0
                                                                ? ensured.IsSuccess && ensured.Value == value
                                                                : ensured.IsFailure
                                                                   && ensured.Error?.Code == ErrorCodes.Validation
                                                                   && ensured.Error?.Message.Contains("odd", StringComparison.OrdinalIgnoreCase) == true;
                                                        }));

    [Fact]
    public void Sequence_ShouldAccumulate_OnSuccess() => Check.QuickThrowOnFailure(Prop.ForAll<int[]>(values =>
                                                              {
                                                                  var results = values.Select(v => Result.Ok(v));
                                                                  var aggregated = Result.Sequence(results);

                                                                  return aggregated.IsSuccess && aggregated.Value.SequenceEqual(values);
                                                              }));

    [Fact]
    public void Recover_ShouldRehydrateFailure() => Check.QuickThrowOnFailure(Prop.ForAll<NonEmptyString>(message =>
                                                         {
                                                             var failure = Result.Fail<int>(Error.From(message.Item, ErrorCodes.Unspecified));

                                                             var recovered = failure.Recover(error => Result.Ok(error.Message.Length));

                                                             return recovered.IsSuccess && recovered.Value == message.Item.Length;
                                                         }));
}
