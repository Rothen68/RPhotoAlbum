import { TestBed } from '@angular/core/testing';
import { MarkdownPipe } from './markdown.pipe';

describe('MarkdownPipe', () => {
  let pipe: MarkdownPipe;

  beforeEach(() => {
    TestBed.configureTestingModule({});
    pipe = TestBed.runInInjectionContext(() => new MarkdownPipe());
  });

  // Régression : un simple retour à la ligne (ex. date en gras suivie d'une légende, tapées sur
  // deux lignes) doit produire un <br> réel, pas être fusionné en un espace (comportement
  // CommonMark par défaut de `marked` sans `breaks: true`) — voir issue #30.
  it('converts a single newline into a real line break', () => {
    const html = String(pipe.transform('**01/01/2020**\nEntrainement au patin à glace'));

    expect(html).toContain('<br>');
    expect(html).not.toMatch(/2020<\/strong>\s*Entrainement/);
  });

  it('renders basic markdown (bold) as HTML', () => {
    const html = String(pipe.transform('**important**'));

    expect(html).toContain('<strong>important</strong>');
  });

  it('returns an empty string for null/undefined/empty input', () => {
    expect(pipe.transform(null)).toBe('');
    expect(pipe.transform(undefined)).toBe('');
    expect(pipe.transform('')).toBe('');
  });
});
