using System;
using System.ComponentModel;
using System.Reflection;
using ImageViewer.Controls;
using ImageViewer.Models;
using Xunit;

namespace ImageViewerControl.Tests
{
    [Trait("Category", "Compatibility")]
    public class ImageViewerCompatibilityShimTests
    {
        [Fact]
        public void OpenImageAsync_CompatibilityShim_IsHiddenObsoleteAndForwardsToShowOpenImageDialogAsync()
        {
            MethodInfo shim = GetRequiredMethod(typeof(ImageViewer.Controls.ImageViewer), nameof(ImageViewer.Controls.ImageViewer.OpenImageAsync));
            MethodInfo replacement = GetRequiredMethod(typeof(ImageViewer.Controls.ImageViewer), nameof(ImageViewer.Controls.ImageViewer.ShowOpenImageDialogAsync));

            AssertHasHiddenObsoleteCompatibilityContract(shim, "Use ShowOpenImageDialogAsync() instead.");
            Assert.True(CallsMethod(shim, replacement));
        }

        [Fact]
        public void ShowRoiPropertiesDialog_CompatibilityShim_IsHiddenObsoleteAndForwardsToShowRoiProperties()
        {
            MethodInfo shim = GetRequiredMethod(typeof(ImageViewer.Controls.ImageViewer), nameof(ImageViewer.Controls.ImageViewer.ShowRoiPropertiesDialog), typeof(RoiBase));
            MethodInfo replacement = GetRequiredMethod(typeof(ImageViewer.Controls.ImageViewer), nameof(ImageViewer.Controls.ImageViewer.ShowRoiProperties), typeof(RoiBase));

            AssertHasHiddenObsoleteCompatibilityContract(shim, "Use ShowRoiProperties(RoiBase) instead.");
            Assert.True(CallsMethod(shim, replacement));
        }

        private static MethodInfo GetRequiredMethod(Type type, string name, params Type[] parameterTypes)
        {
            return type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public, binder: null, parameterTypes, modifiers: null)
                ?? throw new InvalidOperationException($"Method '{name}' was not found on '{type.FullName}'.");
        }

        private static void AssertHasHiddenObsoleteCompatibilityContract(MethodInfo method, string expectedMessage)
        {
            var editorBrowsable = method.GetCustomAttribute<EditorBrowsableAttribute>();
            var obsolete = method.GetCustomAttribute<ObsoleteAttribute>();

            Assert.NotNull(editorBrowsable);
            Assert.Equal(EditorBrowsableState.Never, editorBrowsable!.State);
            Assert.NotNull(obsolete);
            Assert.Equal(expectedMessage, obsolete!.Message);
            Assert.False(obsolete.IsError);
        }

        private static bool CallsMethod(MethodInfo source, MethodInfo target)
        {
            byte[] il = source.GetMethodBody()?.GetILAsByteArray()
                ?? throw new InvalidOperationException($"Method '{source.Name}' has no method body.");

            int targetToken = target.MetadataToken;
            for (int index = 0; index <= il.Length - 5; index++)
            {
                byte opcode = il[index];
                if ((opcode == 0x28 || opcode == 0x6F) && BitConverter.ToInt32(il, index + 1) == targetToken)
                {
                    return true;
                }
            }

            return false;
        }
    }
}