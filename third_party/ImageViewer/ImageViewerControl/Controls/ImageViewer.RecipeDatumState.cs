using System.Collections.Generic;
using ImageViewer.Core.Persistence;
using ImageViewer.Models;

namespace ImageViewer.Controls
{
    public partial class ImageViewer
    {
        private RecipeDatumPoint? _recipeReferenceDatum1;
        private RecipeDatumPoint? _recipeReferenceDatum2;
        private RecipeDatumPoint? _recipeCurrentDatum1;
        private RecipeDatumPoint? _recipeCurrentDatum2;

        internal IReadOnlyList<RecipeDatumPoint> GetRecipeReferenceDatums() =>
            CreateOrderedPair(_recipeReferenceDatum1, _recipeReferenceDatum2);

        internal IReadOnlyList<RecipeDatumPoint> GetRecipeCurrentDatums() =>
            CreateOrderedPair(_recipeCurrentDatum1, _recipeCurrentDatum2);

        internal void SetRecipeReferenceDatums(IReadOnlyList<RecipeDatumPoint> points)
        {
            _recipeReferenceDatum1 = points.Count > 0 ? points[0] : null;
            _recipeReferenceDatum2 = points.Count > 1 ? points[1] : null;
        }

        internal void SetRecipeReferenceDatum(int index, RecipeDatumPoint point)
        {
            if (index == 0)
            {
                _recipeReferenceDatum1 = point;
            }
            else
            {
                _recipeReferenceDatum2 = point;
            }
        }

        internal void SetRecipeCurrentDatum(int index, RecipeDatumPoint point)
        {
            if (index == 0)
            {
                _recipeCurrentDatum1 = point;
            }
            else
            {
                _recipeCurrentDatum2 = point;
            }
        }

        internal void SetRecipeCurrentDatums(IReadOnlyList<RecipeDatumPoint> points)
        {
            _recipeCurrentDatum1 = points.Count > 0 ? points[0] : null;
            _recipeCurrentDatum2 = points.Count > 1 ? points[1] : null;
        }

        internal void ClearRecipeCurrentDatums()
        {
            _recipeCurrentDatum1 = null;
            _recipeCurrentDatum2 = null;
        }

        internal void ClearAllRecipeDatums()
        {
            SetRecipeReferenceDatums(System.Array.Empty<RecipeDatumPoint>());
            ClearRecipeCurrentDatums();
        }

        private static IReadOnlyList<RecipeDatumPoint> CreateOrderedPair(
            RecipeDatumPoint? first,
            RecipeDatumPoint? second)
        {
            if (first is null)
            {
                return System.Array.Empty<RecipeDatumPoint>();
            }

            return second is null ? [first] : [first, second];
        }
    }
}
