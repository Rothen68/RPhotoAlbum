import { Pipe, PipeTransform, inject } from '@angular/core';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { marked } from 'marked';

// bypassSecurityTrustHtml sans sanitisation supplémentaire : app mono-utilisateur (le
// contenu vient de l'utilisateur lui-même), donc hygiène plutôt que frontière de sécurité.
@Pipe({
  name: 'markdown',
  standalone: true,
})
export class MarkdownPipe implements PipeTransform {
  private readonly sanitizer = inject(DomSanitizer);

  transform(value: string | null | undefined): SafeHtml {
    if (!value) {
      return '';
    }
    // breaks: true — un simple retour à la ligne (Entrée) dans l'éditeur devient un vrai saut de
    // ligne rendu, pas seulement un espace (comportement CommonMark par défaut, qui exige une
    // ligne vide entre paragraphes ou deux espaces en fin de ligne pour un saut "dur" — contre-
    // intuitif ici, où la zone de texte est une note libre, pas du Markdown académique).
    return this.sanitizer.bypassSecurityTrustHtml(marked.parse(value, { async: false, breaks: true }) as string);
  }
}
