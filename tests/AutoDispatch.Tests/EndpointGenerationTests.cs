using System.Linq;
using Xunit;

namespace AutoDispatch.Tests;

/// <summary>
/// Tests for minimal API endpoint generation: a command/query type decorated with
/// <c>[Endpoint(method, route)]</c> gets wired directly to an ASP.NET Core minimal API route via
/// a generated <c>MapAutoDispatchEndpoints(this IEndpointRouteBuilder app)</c> extension method —
/// but only when the compilation actually references
/// <c>Microsoft.AspNetCore.Routing.IEndpointRouteBuilder</c>.
/// </summary>
public class EndpointGenerationTests
{
    private const string Usings = @"
using AutoDispatch;
using System.Threading;
using System.Threading.Tasks;
";

    private const string AspNetCoreRoutingStub = @"
namespace Microsoft.AspNetCore.Routing
{
    public interface IEndpointRouteBuilder { }
}

namespace Microsoft.AspNetCore.Http
{
    public sealed class AsParametersAttribute : System.Attribute { }

    public static class Results
    {
        public static object Ok(object? value) => value!;
        public static object NoContent() => new object();
    }
}

namespace Microsoft.AspNetCore.Mvc
{
    public sealed class FromBodyAttribute : System.Attribute { }
}
";

    [Fact]
    public void NoEndpointAttributeUsed_DoesNotGenerateEndpointsSource()
    {
        var sources = DispatchGeneratorTests.RunGenerator(Usings + AspNetCoreRoutingStub + @"
public sealed class CreateOrderCommand { }

[Handler]
public sealed class CreateOrderHandler
{
    public Task<int> HandleAsync(CreateOrderCommand cmd, CancellationToken ct) => Task.FromResult(42);
}", out _);

        Assert.DoesNotContain("AutoDispatch.Endpoints.g.cs", sources.Keys);
    }

    [Fact]
    public void EndpointAttributeUsed_ButAspNetCoreRoutingNotReferenced_DoesNotGenerateEndpointsSource()
    {
        var sources = DispatchGeneratorTests.RunGenerator(Usings + @"
[Endpoint(""POST"", ""/orders"")]
public sealed class CreateOrderCommand { }

[Handler]
public sealed class CreateOrderHandler
{
    public Task<int> HandleAsync(CreateOrderCommand cmd, CancellationToken ct) => Task.FromResult(42);
}", out var diagnostics);

        Assert.DoesNotContain("AutoDispatch.Endpoints.g.cs", sources.Keys);
        Assert.DoesNotContain(diagnostics, d => d.Id is "AD031" or "AD032");
    }

    [Fact]
    public void EndpointAttributeUsed_WithAspNetCoreRoutingReferenced_GeneratesMapAutoDispatchEndpoints()
    {
        var sources = DispatchGeneratorTests.RunGenerator(Usings + AspNetCoreRoutingStub + @"
[Endpoint(""POST"", ""/orders"")]
public sealed class CreateOrderCommand { }

[Handler]
public sealed class CreateOrderHandler
{
    public Task<int> HandleAsync(CreateOrderCommand cmd, CancellationToken ct) => Task.FromResult(42);
}", out var diagnostics);

        Assert.DoesNotContain(diagnostics, d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        var src = sources["AutoDispatch.Endpoints.g.cs"];
        Assert.Contains("public static class AutoDispatchEndpoints", src);
        Assert.Contains("MapAutoDispatchEndpoints", src);
        Assert.Contains("app.MapPost(\"/orders\"", src);
        Assert.Contains("[global::Microsoft.AspNetCore.Mvc.FromBody]", src);
        Assert.Contains("dispatcher.SendAsync(request, ct)", src);
        Assert.Contains("global::Microsoft.AspNetCore.Http.Results.Ok(response)", src);
    }

    [Fact]
    public void GetEndpoint_BindsRequestWithAsParametersInsteadOfFromBody()
    {
        var sources = DispatchGeneratorTests.RunGenerator(Usings + AspNetCoreRoutingStub + @"
[Endpoint(""GET"", ""/orders/{id}"")]
public sealed class GetOrderQuery { public int Id { get; set; } }

[Handler]
public sealed class GetOrderHandler
{
    public Task<int> HandleAsync(GetOrderQuery query, CancellationToken ct) => Task.FromResult(query.Id);
}", out _);

        var src = sources["AutoDispatch.Endpoints.g.cs"];
        Assert.Contains("app.MapGet(\"/orders/{id}\"", src);
        Assert.Contains("[global::Microsoft.AspNetCore.Http.AsParameters]", src);
    }

    [Fact]
    public void DuplicateRouteAndMethod_ReportsAD031()
    {
        DispatchGeneratorTests.RunGenerator(Usings + AspNetCoreRoutingStub + @"
[Endpoint(""POST"", ""/orders"")]
public sealed class CreateOrderCommand { }

[Endpoint(""POST"", ""/orders"")]
public sealed class RenameOrderCommand { }

[Handler]
public sealed class CreateOrderHandler
{
    public Task<int> HandleAsync(CreateOrderCommand cmd, CancellationToken ct) => Task.FromResult(42);
}

[Handler]
public sealed class RenameOrderHandler
{
    public Task<int> HandleAsync(RenameOrderCommand cmd, CancellationToken ct) => Task.FromResult(1);
}", out var diagnostics);

        Assert.Contains(diagnostics, d => d.Id == "AD031");
    }

    [Fact]
    public void EndpointOnTypeWithNoMatchingHandler_ReportsAD032()
    {
        DispatchGeneratorTests.RunGenerator(Usings + AspNetCoreRoutingStub + @"
[Endpoint(""POST"", ""/orders"")]
public sealed class CreateOrderCommand { }

public sealed class UnrelatedCommand { }

[Handler]
public sealed class UnrelatedHandler
{
    public Task<int> HandleAsync(UnrelatedCommand cmd, CancellationToken ct) => Task.FromResult(1);
}
", out var diagnostics);

        Assert.Contains(diagnostics, d => d.Id == "AD032");
    }

    [Fact]
    public void VoidSyncHandler_GeneratesNoContentResponse()
    {
        var sources = DispatchGeneratorTests.RunGenerator(Usings + AspNetCoreRoutingStub + @"
[Endpoint(""DELETE"", ""/orders/{id}"")]
public sealed class DeleteOrderCommand { }

[Handler]
public sealed class DeleteOrderHandler
{
    public void Handle(DeleteOrderCommand cmd) { }
}", out _);

        var src = sources["AutoDispatch.Endpoints.g.cs"];
        Assert.Contains("app.MapDelete(\"/orders/{id}\"", src);
        Assert.Contains("dispatcher.Send(request);", src);
        Assert.Contains("global::Microsoft.AspNetCore.Http.Results.NoContent()", src);
    }

    [Fact]
    public void HandlerWithXmlDocSummary_GeneratesWithSummary()
    {
        var sources = DispatchGeneratorTests.RunGenerator(Usings + AspNetCoreRoutingStub + @"
[Endpoint(""POST"", ""/orders"")]
public sealed class CreateOrderCommand { }

[Handler]
public sealed class CreateOrderHandler
{
    /// <summary>Creates a new order.</summary>
    public Task<int> HandleAsync(CreateOrderCommand cmd, CancellationToken ct) => Task.FromResult(42);
}", out _);

        var src = sources["AutoDispatch.Endpoints.g.cs"];
        Assert.Contains(".WithSummary(\"Creates a new order.\")", src);
    }
}
