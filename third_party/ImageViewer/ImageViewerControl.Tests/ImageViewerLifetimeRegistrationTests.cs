using System;
using System.Collections.Generic;
using ImageViewer.Controls;
using Xunit;

namespace ImageViewerControl.Tests
{
    [Trait("Category", "Unit")]
    public class ImageViewerLifetimeRegistrationTests
    {
        [Fact]
        public void Attach_WhenRegistrationThrows_RollsBackPreviouslyAttachedRegistrations()
        {
            var events = new List<string>();
            var registrations = new ImageViewerLifetimeRegistrationCollection();
            registrations.AddAttachment(() => events.Add("attach-1"), () => events.Add("detach-1"));
            registrations.AddAttachment(() => throw new InvalidOperationException("attach-2"), () => events.Add("detach-2"));

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => registrations.Attach());

            Assert.Equal("attach-2", ex.Message);
            Assert.Equal(["attach-1", "detach-1"], events);
        }

        [Fact]
        public void Attach_WhenRollbackThrows_AggregatesAttachAndRollbackErrors()
        {
            var registrations = new ImageViewerLifetimeRegistrationCollection();
            registrations.AddAttachment(() => { }, () => throw new InvalidOperationException("detach-1"));
            registrations.AddAttachment(() => throw new InvalidOperationException("attach-2"));

            AggregateException ex = Assert.Throws<AggregateException>(() => registrations.Attach());

            Assert.Collection(
                ex.InnerExceptions,
                inner => Assert.Equal("attach-2", inner.Message),
                inner => Assert.Equal("detach-1", inner.Message));
        }

        [Fact]
        public void Dispose_WhenDetachOrCleanupThrows_AggregatesAllFailuresAndContinues()
        {
            var events = new List<string>();
            var registrations = new ImageViewerLifetimeRegistrationCollection();
            registrations.AddAttachment(() => events.Add("attach-1"), () =>
            {
                events.Add("detach-1");
                throw new InvalidOperationException("detach-1");
            });
            registrations.AddAttachment(() => events.Add("attach-2"), () => events.Add("detach-2"));
            registrations.AddCleanup(() =>
            {
                events.Add("cleanup-1");
                throw new InvalidOperationException("cleanup-1");
            });
            registrations.AddCleanup(() => events.Add("cleanup-2"));
            registrations.Attach();

            AggregateException ex = Assert.Throws<AggregateException>(() => registrations.Dispose());

            Assert.Equal(["attach-1", "attach-2", "detach-2", "detach-1", "cleanup-2", "cleanup-1"], events);
            Assert.Collection(
                ex.InnerExceptions,
                inner => Assert.Equal("detach-1", inner.Message),
                inner => Assert.Equal("cleanup-1", inner.Message));
        }
    }
}