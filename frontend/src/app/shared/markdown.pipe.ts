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
    return this.sanitizer.bypassSecurityTrustHtml(marked.parse(value, { async: false }) as string);
  }
}
