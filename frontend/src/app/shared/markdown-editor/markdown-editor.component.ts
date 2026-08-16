import { Component, ElementRef, EventEmitter, Input, Output, ViewChild, signal } from '@angular/core';
import { MarkdownPipe } from '../markdown.pipe';

// Éditeur Markdown minimal réutilisé pour l'ajout et l'édition de blocs texte d'album :
// barre d'outils qui enveloppe/insère autour de la sélection courante du <textarea>
// (selectionStart/selectionEnd), plus une bascule aperçu/édition. Pas de librairie
// d'édition tierce, par choix (§ plan V2 étape 6).
@Component({
  selector: 'app-markdown-editor',
  standalone: true,
  imports: [MarkdownPipe],
  templateUrl: './markdown-editor.component.html',
  styleUrl: './markdown-editor.component.scss',
})
export class MarkdownEditorComponent {
  @Input() value = '';
  @Input() placeholder = '';
  @Output() valueChange = new EventEmitter<string>();
  @Output() blurred = new EventEmitter<void>();

  @ViewChild('ta') private textareaRef?: ElementRef<HTMLTextAreaElement>;

  protected readonly previewMode = signal(false);

  togglePreview(): void {
    this.previewMode.update((v) => !v);
  }

  onInput(text: string): void {
    this.value = text;
    this.valueChange.emit(text);
  }

  wrap(before: string, after: string = before): void {
    const el = this.textareaRef?.nativeElement;
    if (!el) {
      return;
    }
    const start = el.selectionStart ?? 0;
    const end = el.selectionEnd ?? 0;
    const selected = this.value.slice(start, end);
    const next = this.value.slice(0, start) + before + selected + after + this.value.slice(end);
    this.value = next;
    this.valueChange.emit(next);

    queueMicrotask(() => {
      el.focus();
      el.setSelectionRange(start + before.length, start + before.length + selected.length);
    });
  }

  insertLinePrefix(prefix: string): void {
    const el = this.textareaRef?.nativeElement;
    if (!el) {
      return;
    }
    const start = el.selectionStart ?? 0;
    const lineStart = this.value.lastIndexOf('\n', start - 1) + 1;
    const next = this.value.slice(0, lineStart) + prefix + this.value.slice(lineStart);
    this.value = next;
    this.valueChange.emit(next);

    queueMicrotask(() => {
      el.focus();
      el.setSelectionRange(start + prefix.length, start + prefix.length);
    });
  }

  insertLink(): void {
    const el = this.textareaRef?.nativeElement;
    if (!el) {
      return;
    }
    const start = el.selectionStart ?? 0;
    const end = el.selectionEnd ?? 0;
    const selected = this.value.slice(start, end);
    const label = selected || 'texte du lien';
    const before = `[${label}](`;
    const after = ')';
    const next = this.value.slice(0, start) + before + after + this.value.slice(end);
    this.value = next;
    this.valueChange.emit(next);

    const urlPos = start + before.length;
    queueMicrotask(() => {
      el.focus();
      el.setSelectionRange(urlPos, urlPos);
    });
  }
}
