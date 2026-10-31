using System.Xml.Linq;
using Xunit;

namespace KeyboardWtf.Tests;

// Source packaging contract only. Does not establish completed legal clearance,
// final single-file contents or installed acceptance.
public sealed class PublicLegalPackageTests
{
    [Fact]
    public void Canonical_public_flavor_explicitly_keeps_root_license_and_notices_as_visible_files()
    {
        var root = FindRoot();
        var project = XDocument.Load(Path.Combine(root, "src", "KeyboardWtf.csproj"));
        const string publicCondition = "'$(KeyboardWtfPaidProduction)' == 'true' And '$(KeyboardWtfPaidProductionCandidate)' != 'true' And '$(KeyboardWtfPersonalApiCandidate)' != 'true'";
        var publicGroup = Assert.Single(project.Root!.Elements("ItemGroup"),
            element => (string)element.Attribute("Condition") == publicCondition);
        foreach (var file in new[] { "LICENSE", "NOTICES.md" })
        {
            var item = Assert.Single(publicGroup.Elements("Content"),
                element => (string)element.Attribute("Include") == "..\\" + file);
            Assert.Equal(publicCondition, (string)item.Parent.Attribute("Condition"));
            Assert.Equal(file, (string)item.Element("Link"));
            Assert.Equal("PreserveNewest", (string)item.Element("CopyToOutputDirectory"));
            Assert.Equal("PreserveNewest", (string)item.Element("CopyToPublishDirectory"));
            Assert.Equal("true", (string)item.Element("ExcludeFromSingleFile"));
            Assert.True(File.Exists(Path.Combine(root, file)));
        }
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "KeyboardWtf.sln"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("The source packaging contract requires this checkout.");
    }
}
