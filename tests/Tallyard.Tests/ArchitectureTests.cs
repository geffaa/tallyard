using NetArchTest.Rules;

namespace Tallyard.Tests;

/// <summary>Module boundaries, checked on every build rather than left to code review.</summary>
public class ArchitectureTests
{
    static readonly System.Reflection.Assembly Api = typeof(Program).Assembly;
    const string Root = "Tallyard.Api.Modules.";

    // Each module may depend on the ones listed; anything else is a boundary violation.
    static readonly Dictionary<string, string[]> Allowed = new()
    {
        ["Identity"] = ["Warehouses"],
        ["Warehouses"] = [],
        ["Catalog"] = [],
        ["Partners"] = [],
        ["Audit"] = [],
        // Identity only for showing who did what.
        ["Inventory"] = ["Catalog", "Warehouses", "Identity"],
    };

    public static TheoryData<string> Modules => new(Allowed.Keys);

    [Theory]
    [MemberData(nameof(Modules))]
    public void Modules_only_depend_on_what_they_are_allowed_to(string module)
    {
        var forbidden = Allowed.Keys.Where(m => m != module && !Allowed[module].Contains(m)).Select(m => Root + m).ToArray();
        var result = Types.InAssembly(Api).That().ResideInNamespace(Root + module)
            .ShouldNot().HaveDependencyOnAny(forbidden).GetResult();
        Assert.True(result.IsSuccessful, $"{module} depends on: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void Modules_do_not_depend_on_the_seeder()
    {
        var result = Types.InAssembly(Api).That().ResideInNamespace(Root.TrimEnd('.'))
            .ShouldNot().HaveDependencyOn("Tallyard.Api.Seed").GetResult();
        Assert.True(result.IsSuccessful);
    }
}
