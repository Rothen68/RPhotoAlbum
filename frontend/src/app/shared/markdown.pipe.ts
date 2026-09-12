import { Pipe, PipeTransform, inject } from '@angular/core';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { marked } from 'marked';

// bypassSecurityTrustHtml with no extra sanitization: single-user app (the content comes
// from the user themselves), so this is hygiene rather than a security boundary.
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
    // breaks: true — a plain line break (Enter) in the editor becomes an actual rendered line
    // break, not just a space (default CommonMark behavior requires a blank line between
    // paragraphs, or two trailing spaces, for a "hard" break — counterintuitive here, where the
    // text area is a free-form note, not academic Markdown).
    return this.sanitizer.bypassSecurityTrustHtml(marked.parse(value, { async: false, breaks: true }) as string);
  }
}
