using System.Linq;
using Xunit;

namespace AutoDispatch.Tests;

/// <summary>
/// Tests for the automatic FluentValidation integration: whenever a compilation references
/// FluentValidation, AutoDispatch generates a single open-generic pre-processor
/// (<c>AutoDispatchValidationPreProcessor&lt;TCommand&gt;</c>) that is applied to every async
/// command and auto-registers every discovered <c>IValidator&lt;T&gt;</c> implementation in DI —
/// with no attribute or opt-in required on the command or validator class itself.
/// </summary>
public class ValidationBehaviorTests
{
    private const string Usings = @"
using AutoDispatch;
using System.Threading;
using System.Threading.Tasks;
";

    private const string FluentValidationStub = @"
namespace FluentValidation.Results
{
    public class ValidationFailure
    {
        public string PropertyName = """";
        public string ErrorMessage = """";
    }

    public class ValidationResult
    {
        public bool IsValid = true;
        public System.Collections.Generic.IEnumerable<ValidationFailure> Errors = new ValidationFailure[0];
    }
}

namespace FluentValidation
{
    public interface IValidator<in T>
    {
        FluentValidation.Results.ValidationResult Validate(T instance);
        System.Threading.Tasks.Task<FluentValidation.Results.ValidationResult> ValidateAsync(T instance, System.Threading.CancellationToken cancellation = default);
    }

    public class ValidationException : System.Exception
    {
        public ValidationException(System.Collections.Generic.IEnumerable<FluentValidation.Results.ValidationFailure> errors) : base(""Validation failed"") { }
    }
}
";

    [Fact]
    public void NoFluentValidationReferenced_DoesNotGenerateValidationSource()
    {
        var sources = DispatchGeneratorTests.RunGenerator(Usings + @"
public sealed class CreateOrderCommand { }

[Handler]
public sealed class CreateOrderHandler
{
    public Task<int> HandleAsync(CreateOrderCommand cmd, CancellationToken ct) => Task.FromResult(42);
}", out _);

        Assert.DoesNotContain("AutoDispatch.Validation.g.cs", sources.Keys);
        Assert.DoesNotContain("AutoDispatchValidationPreProcessor", sources["AutoDispatch.Registration.g.cs"]);
    }

    [Fact]
    public void FluentValidationReferenced_GeneratesValidationPreProcessor()
    {
        var sources = DispatchGeneratorTests.RunGenerator(Usings + FluentValidationStub + @"
public sealed class CreateOrderCommand { }

[Handler]
public sealed class CreateOrderHandler
{
    public Task<int> HandleAsync(CreateOrderCommand cmd, CancellationToken ct) => Task.FromResult(42);
}", out _);

        Assert.Contains("AutoDispatch.Validation.g.cs", sources.Keys);
        var validationSrc = sources["AutoDispatch.Validation.g.cs"];
        Assert.Contains("AutoDispatchValidationPreProcessor", validationSrc);
        Assert.Contains("IValidator<TCommand>", validationSrc);
        Assert.Contains("ValidationException", validationSrc);
    }

    [Fact]
    public void FluentValidationReferenced_AsyncHandler_DispatcherAppliesPreProcessor()
    {
        var sources = DispatchGeneratorTests.RunGenerator(Usings + FluentValidationStub + @"
public sealed class CreateOrderCommand { }

[Handler]
public sealed class CreateOrderHandler
{
    public Task<int> HandleAsync(CreateOrderCommand cmd, CancellationToken ct) => Task.FromResult(42);
}", out _);

        var dispatcherSrc = sources["AutoDispatch.Dispatcher.g.cs"];
        Assert.Contains("AutoDispatchValidationPreProcessor<", dispatcherSrc);
        Assert.Contains("CreateOrderCommand", dispatcherSrc);
    }

    [Fact]
    public void FluentValidationReferenced_RegistersDiscoveredValidator()
    {
        var sources = DispatchGeneratorTests.RunGenerator(Usings + FluentValidationStub + @"
public sealed class CreateOrderCommand { }

public sealed class CreateOrderCommandValidator : FluentValidation.IValidator<CreateOrderCommand>
{
    public FluentValidation.Results.ValidationResult Validate(CreateOrderCommand instance) => new FluentValidation.Results.ValidationResult();
    public Task<FluentValidation.Results.ValidationResult> ValidateAsync(CreateOrderCommand instance, CancellationToken cancellation = default) => Task.FromResult(new FluentValidation.Results.ValidationResult());
}

[Handler]
public sealed class CreateOrderHandler
{
    public Task<int> HandleAsync(CreateOrderCommand cmd, CancellationToken ct) => Task.FromResult(42);
}", out _);

        var registrationSrc = sources["AutoDispatch.Registration.g.cs"];
        Assert.Contains("IValidator<", registrationSrc);
        Assert.Contains("CreateOrderCommand", registrationSrc);
        Assert.Contains("CreateOrderCommandValidator", registrationSrc);
        Assert.Contains("AutoDispatchValidationPreProcessor<>", registrationSrc);
    }

    [Fact]
    public void FluentValidationReferenced_SyncHandler_IsNotWrapped()
    {
        // Pre-processors (including the automatic validation one) only apply to async handlers —
        // this mirrors the existing, documented [PreProcessor]/[PostProcessor] limitation.
        var sources = DispatchGeneratorTests.RunGenerator(Usings + FluentValidationStub + @"
public sealed class CreateOrderCommand { }

[Handler]
public sealed class CreateOrderHandler
{
    public int Handle(CreateOrderCommand cmd) => 42;
}", out _);

        var dispatcherSrc = sources["AutoDispatch.Dispatcher.g.cs"];
        Assert.DoesNotContain("AutoDispatchValidationPreProcessor", dispatcherSrc);
    }
}
