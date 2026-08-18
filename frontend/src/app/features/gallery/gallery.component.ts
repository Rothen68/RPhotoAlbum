import {
  AfterViewInit,
  Component,
  ElementRef,
  NgZone,
  OnDestroy,
  OnInit,
  ViewChild,
  computed,
  inject,
  signal,
} from '@angular/core';
import { ScrollingModule } from '@angular/cdk/scrolling';
import { Subscription } from 'rxjs';
import { DateGroup, LocationCombo, MediaFilters, MediaItem, MediaService } from '../../core/media/media.service';
import { AddToAlbumSheetComponent } from '../../shared/add-to-album-sheet/add-to-album-sheet.component';
import { LongPressDirective } from '../../shared/long-press.directive';
import { MediaViewerComponent } from '../../shared/media-viewer/media-viewer.component';
import { GalleryDataSource, GalleryVirtualScrollDirective, VirtualRow, buildRows, formatDateLabel } from './gallery-virtual';

const PAGE_SIZE = 60;
const COLUMNS_STORAGE_KEY = 'rphotoalbum.gallery.columns';
const HEADER_ROW_HEIGHT_PX = 40;
const GRID_GAP_PX = 6;

@Component({
  selector: 'app-gallery',
  standalone: true,
  imports: [
    AddToAlbumSheetComponent,
    MediaViewerComponent,
    LongPressDirective,
    ScrollingModule,
    GalleryVirtualScrollDirective,
  ],
  templateUrl: './gallery.component.html',
  styleUrl: './gallery.component.scss',
})
export class GalleryComponent implements OnInit, AfterViewInit, OnDestroy {
  private readonly mediaService = inject(MediaService);
  private readonly hostEl = inject(ElementRef<HTMLElement>);
  private readonly ngZone = inject(NgZone);

  @ViewChild(GalleryVirtualScrollDirective) private scrollStrategy?: GalleryVirtualScrollDirective;

  protected readonly dateGroups = signal<DateGroup[]>([]);
  protected readonly rows = signal<VirtualRow[]>([]);
  protected readonly loadedItems = signal<Map<number, MediaItem>>(new Map());
  protected readonly columns = signal(this.loadStoredColumns());
  protected readonly containerWidth = signal(0);
  protected readonly rowHeightPx = computed(() => {
    const width = this.containerWidth();
    const cols = this.columns();
    if (width <= 0 || cols <= 0) {
      return 100;
    }
    return (width - GRID_GAP_PX * (cols - 1)) / cols + GRID_GAP_PX;
  });

  protected readonly headerHeightPx = HEADER_ROW_HEIGHT_PX;
  // Snapshot des hauteurs effectivement poussées à la stratégie de scroll — le template lit
  // CE tableau (pas rowHeightPx() recalculé en direct) pour rester en permanence synchronisé
  // avec les offsets cumulés de la stratégie, même quand containerWidth change entre-temps.
  protected readonly rowHeights = signal<number[]>([]);
  protected readonly columnOptions = [1, 2, 3, 4];

  protected readonly selectionMode = signal(false);
  protected readonly selectedIds = signal<Set<number>>(new Set());
  protected readonly showAddToAlbumSheet = signal(false);
  protected readonly rejecting = signal(false);
  protected readonly selectedFileIdsArray = computed(() => [...this.selectedIds()]);

  private readonly sortedLoadedEntries = computed(() =>
    [...this.loadedItems().entries()].sort((a, b) => a[0] - b[0]),
  );
  protected readonly viewerItems = computed(() =>
    this.sortedLoadedEntries().map(([, item]) => ({ fileId: item.pCloudFileId, mediaType: item.mediaType })),
  );
  protected readonly viewerIndex = signal<number | null>(null);

  protected readonly dataSource = new GalleryDataSource(
    [],
    PAGE_SIZE,
    (page) => this.mediaService.source(page, PAGE_SIZE, this.currentFilters()),
    (page, items) => this.onPageLoaded(page, items),
  );

  protected readonly searchText = signal('');
  protected readonly mediaTypeFilter = signal<'' | 'image' | 'video'>('');
  protected readonly minSizeFilter = signal<number | undefined>(undefined);

  // Issue #4 : le panneau de filtres est replié par défaut (gain de place sur mobile) — les
  // filtres actifs restent visibles en lecture seule sous forme de puces quand il est fermé.
  protected readonly filtersOpen = signal(false);
  private static readonly SIZE_OPTIONS: { value: number; label: string }[] = [
    { value: 5242880, label: '> 5 Mo' },
    { value: 20971520, label: '> 20 Mo' },
    { value: 52428800, label: '> 50 Mo' },
  ];
  protected readonly sizeOptions = GalleryComponent.SIZE_OPTIONS;

  protected readonly activeFilterChips = computed(() => {
    const chips: string[] = [];
    if (this.mediaTypeFilter()) {
      chips.push(`Type : ${this.mediaTypeFilter() === 'image' ? 'Photo' : 'Vidéo'}`);
    }
    const size = this.minSizeFilter();
    if (size !== undefined) {
      const match = GalleryComponent.SIZE_OPTIONS.find((o) => o.value === size);
      if (match) {
        chips.push(`Taille : ${match.label}`);
      }
    }
    if (this.countryFilter()) {
      chips.push(`Pays : ${this.countryFilter()}`);
    }
    if (this.regionFilter()) {
      chips.push(`Région : ${this.regionFilter()}`);
    }
    if (this.cityFilter()) {
      chips.push(`Ville : ${this.cityFilter()}`);
    }
    return chips;
  });

  // Peuplées uniquement avec des valeurs déjà résolues (étape 9) — pas de texte libre, un pays
  // mal orthographié ne retournerait simplement rien.
  protected readonly locationCombos = signal<LocationCombo[]>([]);
  protected readonly countryFilter = signal('');
  protected readonly regionFilter = signal('');
  protected readonly cityFilter = signal('');

  // Issue #10 : filtres dépendants — un pays sélectionné restreint les régions/villes proposées
  // à ce pays (sinon on pouvait combiner "Allemagne" + une ville française et n'obtenir aucun
  // résultat). Dérivés du même jeu de combinaisons plutôt que trois listes indépendantes.
  protected readonly availableCountries = computed(() =>
    this.distinctSorted(this.locationCombos().map((c) => c.country)),
  );
  protected readonly availableRegions = computed(() => {
    const country = this.countryFilter();
    const combos = country ? this.locationCombos().filter((c) => c.country === country) : this.locationCombos();
    return this.distinctSorted(combos.map((c) => c.region));
  });
  protected readonly availableCities = computed(() => {
    const country = this.countryFilter();
    const region = this.regionFilter();
    const combos = this.locationCombos().filter(
      (c) => (!country || c.country === country) && (!region || c.region === region),
    );
    return this.distinctSorted(combos.map((c) => c.city));
  });

  private distinctSorted(values: (string | null)[]): string[] {
    return [...new Set(values.filter((v): v is string => !!v))].sort();
  }

  private searchDebounceTimer?: ReturnType<typeof setTimeout>;

  private resizeObserver?: ResizeObserver;
  private scrolledIndexSub?: Subscription;
  private currentTopDate: string | null = null;

  ngOnInit(): void {
    this.mediaService.dateGroups(this.currentFilters()).subscribe((groups) => {
      this.dateGroups.set(groups);
      this.recomputeRows();
    });
    this.mediaService.locations().subscribe((combos) => this.locationCombos.set(combos));
  }

  ngAfterViewInit(): void {
    // Suit uniquement la date visible en haut (currentTopDate, un champ simple, pas un signal)
    // pour resynchroniser le scroll après un changement de colonnes — pas besoin de ngZone.run
    // ici puisque rien n'en dépend pour le rendu.
    this.scrolledIndexSub = this.scrollStrategy?.scrolledIndexChange.subscribe((index) => {
      const row = this.rows()[index];
      if (row) {
        this.currentTopDate = row.date;
      }
    });

    // Mesure synchrone initiale : ne pas dépendre uniquement de ResizeObserver, dont le
    // premier déclenchement n'est pas garanti immédiat (et s'exécute hors zone Angular —
    // ngZone.run nécessaire pour que la vue reflète le nouveau signal).
    const initialWidth = this.hostEl.nativeElement.getBoundingClientRect().width;
    if (initialWidth > 0) {
      this.containerWidth.set(initialWidth);
      this.pushHeightsToStrategy();
    }

    this.resizeObserver = new ResizeObserver((entries) => {
      const width = entries[0]?.contentRect.width ?? 0;
      if (width > 0 && width !== this.containerWidth()) {
        this.ngZone.run(() => {
          this.containerWidth.set(width);
          this.pushHeightsToStrategy();
        });
      }
    });
    this.resizeObserver.observe(this.hostEl.nativeElement);
  }

  ngOnDestroy(): void {
    this.resizeObserver?.disconnect();
    this.scrolledIndexSub?.unsubscribe();
    clearTimeout(this.searchDebounceTimer);
  }

  setColumns(count: number): void {
    this.columns.set(count);
    localStorage.setItem(COLUMNS_STORAGE_KEY, String(count));
    this.recomputeRows();
  }

  private loadStoredColumns(): number {
    const stored = Number(localStorage.getItem(COLUMNS_STORAGE_KEY));
    return stored >= 1 && stored <= 4 ? stored : 3;
  }

  // Reconstruit entièrement la structure de rangées (date-groups et/ou colonnes ont changé) —
  // recale la table de tailles et resynchronise le scroll sur la date visible avant le changement.
  private recomputeRows(): void {
    const targetDate = this.currentTopDate;
    const newRows = buildRows(this.dateGroups(), this.columns());
    this.rows.set(newRows);
    this.dataSource.setRows(newRows);
    this.pushHeightsToStrategy();

    if (targetDate) {
      const index = newRows.findIndex((r) => r.date === targetDate);
      if (index >= 0) {
        setTimeout(() => this.scrollStrategy?.scrollToIndex(index, 'auto'));
      }
    }
  }

  // Recalcule uniquement les hauteurs (largeur de conteneur changée, structure inchangée).
  private pushHeightsToStrategy(): void {
    const heights = this.rows().map((r) => (r.type === 'header' ? this.headerHeightPx : this.rowHeightPx()));
    this.rowHeights.set(heights);
    this.scrollStrategy?.updateRowHeights(heights);
  }

  private onPageLoaded(page: number, items: MediaItem[]): void {
    this.loadedItems.update((current) => {
      const next = new Map(current);
      items.forEach((item, i) => next.set((page - 1) * PAGE_SIZE + i, item));
      return next;
    });
  }

  formatDateLabel(isoDate: string): string {
    return formatDateLabel(isoDate);
  }

  thumbnailUrl(fileId: number): string {
    return this.mediaService.thumbnailUrl(fileId);
  }

  onSearchInput(value: string): void {
    this.searchText.set(value);
    clearTimeout(this.searchDebounceTimer);
    this.searchDebounceTimer = setTimeout(() => this.reloadFromScratch(), 300);
  }

  setMediaTypeFilter(value: '' | 'image' | 'video'): void {
    this.mediaTypeFilter.set(value);
    this.reloadFromScratch();
  }

  setMinSizeFilter(value: string): void {
    this.minSizeFilter.set(value ? Number(value) : undefined);
    this.reloadFromScratch();
  }

  setCountryFilter(value: string): void {
    this.countryFilter.set(value);
    // Le pays a changé : région/ville sélectionnées peuvent ne plus lui appartenir.
    if (this.regionFilter() && !this.availableRegions().includes(this.regionFilter())) {
      this.regionFilter.set('');
    }
    if (this.cityFilter() && !this.availableCities().includes(this.cityFilter())) {
      this.cityFilter.set('');
    }
    this.reloadFromScratch();
  }

  setRegionFilter(value: string): void {
    this.regionFilter.set(value);
    if (this.cityFilter() && !this.availableCities().includes(this.cityFilter())) {
      this.cityFilter.set('');
    }
    this.reloadFromScratch();
  }

  setCityFilter(value: string): void {
    this.cityFilter.set(value);
    this.reloadFromScratch();
  }

  toggleFilters(): void {
    this.filtersOpen.update((open) => !open);
  }

  private currentFilters(): MediaFilters {
    const filters: MediaFilters = {};
    const search = this.searchText().trim();
    if (search) {
      filters.search = search;
    }
    if (this.mediaTypeFilter()) {
      filters.mediaType = this.mediaTypeFilter() as 'image' | 'video';
    }
    if (this.minSizeFilter() !== undefined) {
      filters.minSize = this.minSizeFilter();
    }
    if (this.countryFilter()) {
      filters.country = this.countryFilter();
    }
    if (this.regionFilter()) {
      filters.region = this.regionFilter();
    }
    if (this.cityFilter()) {
      filters.city = this.cityFilter();
    }
    return filters;
  }

  enterSelectionMode(): void {
    this.selectionMode.set(true);
  }

  cancelSelection(): void {
    this.selectionMode.set(false);
    this.selectedIds.set(new Set());
  }

  onTileClick(media: MediaItem, flatIndex: number): void {
    if (this.selectionMode()) {
      this.toggleSelect(media);
      return;
    }

    const viewerIndex = this.sortedLoadedEntries().findIndex(([key]) => key === flatIndex);
    if (viewerIndex >= 0) {
      this.viewerIndex.set(viewerIndex);
    }
  }

  posterUrlFn = (fileId: number): string => this.mediaService.thumbnailUrl(fileId);
  imageUrlFn = (fileId: number): string => this.mediaService.thumbnailUrl(fileId, 1600, false);
  streamUrlFn = (fileId: number): string => this.mediaService.streamUrl(fileId);
  downloadUrlFn = (fileId: number): string => this.mediaService.downloadUrl(fileId);

  onLongPress(media: MediaItem): void {
    if (!this.selectionMode()) {
      this.enterSelectionMode();
    }
    this.toggleSelect(media);
  }

  private toggleSelect(media: MediaItem): void {
    this.selectedIds.update((current) => {
      const next = new Set(current);
      if (next.has(media.pCloudFileId)) {
        next.delete(media.pCloudFileId);
      } else {
        next.add(media.pCloudFileId);
      }
      return next;
    });
  }

  isSelected(media: MediaItem): boolean {
    return this.selectedIds().has(media.pCloudFileId);
  }

  reject(): void {
    const fileIds = [...this.selectedIds()];
    if (fileIds.length === 0) {
      return;
    }

    this.rejecting.set(true);
    this.mediaService.reject(fileIds).subscribe({
      next: () => {
        this.rejecting.set(false);
        this.cancelSelection();
        this.reloadFromScratch();
      },
      error: () => this.rejecting.set(false),
    });
  }

  // Toute action changeant l'ensemble/l'ordre des médias (filtre, recherche, rejet en masse)
  // décale les index à plat sous-jacents — on ne peut pas corriger le cache en place sans
  // risquer un décalage silencieux : rechargement complet depuis date-groups.
  private reloadFromScratch(): void {
    this.dataSource.invalidateMediaCache();
    this.loadedItems.set(new Map());
    this.currentTopDate = null;
    this.mediaService.dateGroups(this.currentFilters()).subscribe((groups) => {
      this.dateGroups.set(groups);
      this.recomputeRows();
      this.scrollStrategy?.scrollToIndex(0, 'auto');
    });
  }

  openAddToAlbum(): void {
    if (this.selectedIds().size === 0) {
      return;
    }
    this.showAddToAlbumSheet.set(true);
  }

  onSheetClosed(): void {
    this.showAddToAlbumSheet.set(false);
    this.cancelSelection();
  }
}
