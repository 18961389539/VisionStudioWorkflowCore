using Xunit;

namespace ImageViewerControl.Tests
{
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class WpfTestCollection
    {
        public const string Name = "WPF UI";
    }
}