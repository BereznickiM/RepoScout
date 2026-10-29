using RepoScout.Api.Agent;

namespace RepoScout.Api.UnitTests.Agent;

public class NoiseFilterTests
{
    [Theory]
    [InlineData("node_modules/x/index.js")]
    [InlineData("web/node_modules/x.js")]
    [InlineData("dist/app.js")]
    [InlineData("build/out.txt")]
    [InlineData("src/bin/Debug/a.txt")]
    [InlineData("obj/x.cs")]
    [InlineData(".git/config")]
    [InlineData("vendor/lib.php")]
    [InlineData("coverage/lcov.info")]
    [InlineData("NODE_MODULES/x.js")]
    [InlineData("package-lock.json")]
    [InlineData("web/yarn.lock")]
    [InlineData("pnpm-lock.yaml")]
    [InlineData("packages.lock.json")]
    [InlineData("Cargo.lock")]
    [InlineData("app.min.js")]
    [InlineData("site.min.css")]
    [InlineData("img/logo.PNG")]
    [InlineData("fonts/a.woff2")]
    [InlineData("a.zip")]
    [InlineData("lib/x.dll")]
    [InlineData("doc.pdf")]
    public void IsNoise_NoisePath_ReturnsTrue(string path)
    {
        Assert.True(NoiseFilter.IsNoise(path), path);
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("src/Program.cs")]
    [InlineData("package.json")]
    [InlineData("src/distribution/a.cs")]
    [InlineData("src/builder.cs")]
    [InlineData("Makefile")]
    [InlineData("app.js")]
    [InlineData("styles.css")]
    public void IsNoise_SourcePath_ReturnsFalse(string path)
    {
        Assert.False(NoiseFilter.IsNoise(path), path);
    }
}
