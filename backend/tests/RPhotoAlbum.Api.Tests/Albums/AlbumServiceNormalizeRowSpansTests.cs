using RPhotoAlbum.Api.Albums;

namespace RPhotoAlbum.Api.Tests.Albums;

// Covers AlbumService.NormalizeRowSpans — the validation applied on EVERY write of
// album.json (see the comment on the method). Area deliberately prioritized for the
// project's first tests (GitHub issue #17): pure logic, no I/O, but where a silent
// regression would durably corrupt album.json — not a rebuildable cache, the real
// source of truth lives on pCloud.
public class AlbumServiceNormalizeRowSpansTests
{
    [Fact]
    public void EmptyDocument_DoesNothing()
    {
        var doc = Doc();

        AlbumService.NormalizeRowSpans(doc);

        Assert.Empty(doc.Items);
    }

    [Fact]
    public void SingleMediaItem_RowSpanClampedTo1_NoNeighbors()
    {
        var doc = Doc(Media("a", rowSpan: 3));

        AlbumService.NormalizeRowSpans(doc);

        Assert.Equal(1, doc.Items[0].RowSpan);
    }

    [Fact]
    public void SingleMediaItem_RowSpanZeroOrNegative_ClampedUpTo1()
    {
        var doc = Doc(Media("a", rowSpan: 0));

        AlbumService.NormalizeRowSpans(doc);

        Assert.Equal(1, doc.Items[0].RowSpan);
    }

    [Fact]
    public void ThreeConsecutiveMedia_AnchorRowSpan3_KeptAsIs()
    {
        var doc = Doc(Media("a", rowSpan: 3), Media("b"), Media("c"));

        AlbumService.NormalizeRowSpans(doc);

        Assert.Equal(3, doc.Items[0].RowSpan);
    }

    [Fact]
    public void ThreeConsecutiveMedia_AnchorRowSpan3_FollowersResetTo1()
    {
        // The "follower" items of a group may carry a stale/insignificant RowSpan value
        // (see comment on AlbumItemDocument.RowSpan: only the anchor matters) —
        // NormalizeRowSpans must explicitly reset them to 1 rather than leaving an
        // inconsistent value lingering in album.json.
        var doc = Doc(Media("a", rowSpan: 3), Media("b", rowSpan: 3), Media("c", rowSpan: 3));

        AlbumService.NormalizeRowSpans(doc);

        Assert.Equal(1, doc.Items[1].RowSpan);
        Assert.Equal(1, doc.Items[2].RowSpan);
    }

    [Fact]
    public void FourConsecutiveMedia_AnchorRowSpanAboveMax_ClampedTo3()
    {
        var doc = Doc(Media("a", rowSpan: 10), Media("b"), Media("c"), Media("d"));

        AlbumService.NormalizeRowSpans(doc);

        Assert.Equal(3, doc.Items[0].RowSpan);
        // The 4th media item does not belong to the anchor's group — its own default value (1)
        // is not affected by THIS group (it becomes the anchor of the next group).
        Assert.Equal(1, doc.Items[3].RowSpan);
    }

    [Fact]
    public void TwoConsecutiveMedia_AnchorRowSpan3_ClampedToAvailable2()
    {
        // The cap isn't just 3: it can never exceed the actual number of consecutive media
        // items available after the anchor, even if the stored RowSpan is <= 3.
        var doc = Doc(Media("a", rowSpan: 3), Media("b"));

        AlbumService.NormalizeRowSpans(doc);

        Assert.Equal(2, doc.Items[0].RowSpan);
    }

    [Fact]
    public void MediaFollowedByText_AnchorRowSpanReducedTo1()
    {
        // Case explicitly mentioned in the method's comment: a text block inserted in the
        // middle of a grouped row breaks contiguity — the anchor must fall back to 1, not
        // keep a value that would wrongly encompass the following text block.
        var doc = Doc(Media("a", rowSpan: 3), Text("t"), Media("b"), Media("c"));

        AlbumService.NormalizeRowSpans(doc);

        Assert.Equal(1, doc.Items[0].RowSpan);
    }

    [Fact]
    public void TextItem_RowSpanAlwaysResetTo1()
    {
        var doc = Doc(Text("t", rowSpan: 3));

        AlbumService.NormalizeRowSpans(doc);

        Assert.Equal(1, doc.Items[0].RowSpan);
    }

    [Fact]
    public void MultipleIndependentGroups_EachNormalizedSeparately()
    {
        // Group of 2, then a single media item, then a group of 3 — each group must be
        // evaluated independently (the algorithm advances by `span` at a time, not item by item).
        var doc = Doc(
            Media("a", rowSpan: 2), Media("b"),
            Media("c", rowSpan: 1),
            Media("d", rowSpan: 3), Media("e"), Media("f"));

        AlbumService.NormalizeRowSpans(doc);

        Assert.Equal(2, doc.Items[0].RowSpan);
        Assert.Equal(1, doc.Items[2].RowSpan);
        Assert.Equal(3, doc.Items[3].RowSpan);
    }

    [Fact]
    public void AnchorRemoved_NextItemBecomesNewAnchor_StaysAt1()
    {
        // Simulates the result of a deletion: the anchor of a former group of 3 has disappeared
        // from the list, the item that followed it (RowSpan=1 by default, never significant as
        // long as it wasn't an anchor) becomes the new anchor. NormalizeRowSpans only validates/
        // reduces an existing value, never enlarges it — so it stays at 1 (no automatic
        // absorption of the space left by the deleted anchor; regrouping is an explicit user
        // action, not a consistency fix).
        var doc = Doc(Media("b", rowSpan: 1), Media("c", rowSpan: 1));

        AlbumService.NormalizeRowSpans(doc);

        Assert.Equal(1, doc.Items[0].RowSpan);
    }

    private static AlbumItemDocument Media(string id, int rowSpan = 1) => new()
    {
        Id = id,
        Type = "media",
        RowSpan = rowSpan,
    };

    private static AlbumItemDocument Text(string id, int rowSpan = 1) => new()
    {
        Id = id,
        Type = "text",
        RowSpan = rowSpan,
    };

    private static AlbumDocument Doc(params AlbumItemDocument[] items) => new()
    {
        Id = "alb_test",
        Slug = "test",
        Name = "Test",
        AlbumFolder = new AlbumFolderRef { FolderId = 1, Path = "/test" },
        Items = [.. items],
    };
}
