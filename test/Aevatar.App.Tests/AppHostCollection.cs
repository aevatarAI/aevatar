using Xunit;

namespace Aevatar.App.Tests;

[CollectionDefinition("AppHost")]
public sealed class AppHostCollection : ICollectionFixture<AppHostFixture>
{
}
