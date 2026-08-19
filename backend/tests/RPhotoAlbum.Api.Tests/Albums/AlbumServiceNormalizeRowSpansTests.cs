using RPhotoAlbum.Api.Albums;

namespace RPhotoAlbum.Api.Tests.Albums;

// Couvre AlbumService.NormalizeRowSpans — la validation appliquée à CHAQUE écriture de
// album.json (voir le commentaire sur la méthode). Zone volontairement priorisée pour les
// premiers tests du projet (issue GitHub #17) : logique pure, sans I/O, mais dont une régression
// silencieuse corromprait durablement album.json — pas un cache reconstructible, la source de
// vérité réelle vit sur pCloud.
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
        // Les items "suiveurs" d'un groupe peuvent porter une valeur RowSpan obsolète/non
        // significative (voir commentaire sur AlbumItemDocument.RowSpan : seule l'ancre compte) —
        // NormalizeRowSpans doit les remettre explicitement à 1 plutôt que de laisser une valeur
        // incohérente traîner dans album.json.
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
        // Le 4e média n'appartient pas au groupe de l'ancre — sa propre valeur par défaut (1)
        // n'est pas touchée par CE groupe (il devient l'ancre du groupe suivant).
        Assert.Equal(1, doc.Items[3].RowSpan);
    }

    [Fact]
    public void TwoConsecutiveMedia_AnchorRowSpan3_ClampedToAvailable2()
    {
        // Le plafond n'est pas seulement 3 : il ne peut jamais dépasser le nombre réel d'items
        // média consécutifs disponibles derrière l'ancre, même si RowSpan stocké est <= 3.
        var doc = Doc(Media("a", rowSpan: 3), Media("b"));

        AlbumService.NormalizeRowSpans(doc);

        Assert.Equal(2, doc.Items[0].RowSpan);
    }

    [Fact]
    public void MediaFollowedByText_AnchorRowSpanReducedTo1()
    {
        // Cas cité explicitement dans le commentaire de la méthode : un bloc texte inséré au
        // milieu d'une rangée groupée casse la contiguïté — l'ancre doit retomber à 1, pas
        // garder une valeur qui engloberait à tort le bloc texte suivant.
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
        // Groupe de 2, puis un média seul, puis un groupe de 3 — chaque groupe doit être évalué
        // indépendamment (l'algorithme avance de `span` en `span`, pas item par item).
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
        // Simule le résultat d'une suppression : l'ancre d'un ancien groupe de 3 a disparu de la
        // liste, l'item qui la suivait (RowSpan=1 par défaut, jamais significatif tant qu'il
        // n'était pas ancre) devient la nouvelle ancre. NormalizeRowSpans ne fait QUE valider/
        // réduire une valeur existante, jamais l'agrandir — il reste donc à 1 (pas d'absorption
        // automatique de l'espace laissé par l'ancre supprimée ; regrouper est une action
        // explicite de l'utilisateur, pas une correction de cohérence).
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
