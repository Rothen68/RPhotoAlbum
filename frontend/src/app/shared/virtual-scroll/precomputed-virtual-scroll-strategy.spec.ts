import { CdkVirtualScrollViewport } from '@angular/cdk/scrolling';
import { PrecomputedVirtualScrollStrategy } from './precomputed-virtual-scroll-strategy';

// Faux viewport minimal : seules les méthodes réellement appelées par la stratégie (voir
// précomputed-virtual-scroll-strategy.ts) sont implémentées — measureScrollOffset/
// getViewportSize sont pilotées directement par le test plutôt que de simuler un vrai DOM.
function createFakeViewport(scrollOffset: number, viewportSize: number) {
  return {
    setTotalContentSize: vi.fn(),
    setRenderedRange: vi.fn(),
    setRenderedContentOffset: vi.fn(),
    measureScrollOffset: vi.fn(() => scrollOffset),
    getViewportSize: vi.fn(() => viewportSize),
    scrollToOffset: vi.fn(),
  };
}

describe('PrecomputedVirtualScrollStrategy', () => {
  let strategy: PrecomputedVirtualScrollStrategy;

  beforeEach(() => {
    strategy = new PrecomputedVirtualScrollStrategy();
  });

  // 3 rangées de 100px -> offsets cumulés [0, 100, 200, 300].
  function attachThreeEqualRows(scrollOffset: number, viewportSize = 50) {
    const viewport = createFakeViewport(scrollOffset, viewportSize);
    strategy.attach(viewport as unknown as CdkVirtualScrollViewport);
    strategy.updateRowHeights([100, 100, 100]);
    return viewport;
  }

  it('reports row 0 when scrolled to the top', () => {
    const emitted: number[] = [];
    strategy.scrolledIndexChange.subscribe((i) => emitted.push(i));

    attachThreeEqualRows(0);

    expect(emitted).toEqual([0]);
  });

  it('finds the row containing a mid-row offset', () => {
    const emitted: number[] = [];
    strategy.scrolledIndexChange.subscribe((i) => emitted.push(i));

    attachThreeEqualRows(250);

    expect(emitted).toEqual([2]);
  });

  // Cas limite : un offset qui tombe EXACTEMENT sur la frontière entre deux rangées doit être
  // attribué à la rangée qui commence à cet offset (recherche binaire : dernière rangée dont le
  // départ est <= offset), pas à la précédente.
  it('assigns an exact row-boundary offset to the row that starts there', () => {
    const emitted: number[] = [];
    strategy.scrolledIndexChange.subscribe((i) => emitted.push(i));

    attachThreeEqualRows(200);

    expect(emitted).toEqual([2]);
  });

  it('clamps an offset past the end of the content to the last row', () => {
    const emitted: number[] = [];
    strategy.scrolledIndexChange.subscribe((i) => emitted.push(i));

    attachThreeEqualRows(1000);

    expect(emitted).toEqual([2]);
  });

  it('does not re-emit the same row index on consecutive scroll updates (distinctUntilChanged)', () => {
    // distinctUntilChanged() est ré-évalué par abonnement (pas un état partagé sur le Subject
    // source) : on s'abonne d'abord pour établir une base de comparaison propre à cet abonné.
    const emitted: number[] = [];
    const viewport = attachThreeEqualRows(210);
    strategy.scrolledIndexChange.subscribe((i) => emitted.push(i));

    viewport.measureScrollOffset.mockReturnValue(210); // toujours rangée 2 -> première valeur vue par cet abonné, passe.
    strategy.onContentScrolled();
    expect(emitted).toEqual([2]);

    // Toujours dans la rangée 2 (départ 200) : ne doit pas re-émettre 2.
    viewport.measureScrollOffset.mockReturnValue(220);
    strategy.onContentScrolled();
    viewport.measureScrollOffset.mockReturnValue(230);
    strategy.onContentScrolled();
    expect(emitted).toEqual([2]);

    // Change réellement de rangée : doit émettre.
    viewport.measureScrollOffset.mockReturnValue(0);
    strategy.onContentScrolled();
    expect(emitted).toEqual([2, 0]);
  });

  it('scrollToIndex updates the rendered range synchronously, without waiting for a scroll event', () => {
    const viewport = attachThreeEqualRows(0);
    viewport.setRenderedRange.mockClear();

    strategy.scrollToIndex(1, 'smooth');

    expect(viewport.scrollToOffset).toHaveBeenCalledWith(100, 'smooth');
    expect(viewport.setRenderedRange).toHaveBeenCalled();
  });

  it('clamps scrollToIndex to a valid row range', () => {
    const viewport = attachThreeEqualRows(0);

    strategy.scrollToIndex(99, 'auto');

    // 4 offsets cumulés (3 rangées) -> dernier index valide = 3 (fin du contenu).
    expect(viewport.scrollToOffset).toHaveBeenCalledWith(300, 'auto');
  });
});
