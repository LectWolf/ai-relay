import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, SecurityContext, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { DomSanitizer } from '@angular/platform-browser';
import MarkdownIt from 'markdown-it';
import { ConfirmationService, MessageService } from 'primeng/api';
import { ButtonModule } from 'primeng/button';
import { CardModule } from 'primeng/card';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { TagModule } from 'primeng/tag';
import { TooltipModule } from 'primeng/tooltip';
import { catchError, filter, finalize, of, switchMap, take, timer } from 'rxjs';

import { SystemUpdateInfoDto, SystemVersionDto } from '../../models/system-update.dto';
import { SystemUpdateService } from '../../services/system-update-service';

@Component({
  selector: 'app-settings',
  standalone: true,
  imports: [CommonModule, CardModule, ButtonModule, TagModule, ConfirmDialogModule, TooltipModule],
  providers: [ConfirmationService],
  templateUrl: './settings.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  styles: [
    `
      .release-markdown {
        color: inherit;
        line-height: 1.7;
        word-break: break-word;
      }
      .release-markdown > :first-child { margin-top: 0; }
      .release-markdown > :last-child { margin-bottom: 0; }
      .release-markdown p,
      .release-markdown ul,
      .release-markdown ol,
      .release-markdown pre,
      .release-markdown blockquote,
      .release-markdown h1,
      .release-markdown h2,
      .release-markdown h3 { margin: 0 0 0.75rem; }
      .release-markdown ul,
      .release-markdown ol { padding-left: 1.25rem; }
      .release-markdown pre {
        overflow: auto;
        border-radius: 0.75rem;
        background: color-mix(in srgb, var(--p-surface-900) 88%, transparent);
        padding: 0.75rem 1rem;
        color: var(--p-surface-0);
      }
      .release-markdown code {
        font-family: 'JetBrains Mono', 'Consolas', monospace;
        font-size: 0.9em;
      }
      .release-markdown :not(pre) > code {
        border-radius: 0.4rem;
        background: color-mix(in srgb, var(--p-surface-400) 12%, transparent);
        padding: 0.12rem 0.32rem;
      }
      .release-markdown a { color: var(--p-primary-color); }
    `
  ]
})
export class Settings implements OnInit {
  private readonly service = inject(SystemUpdateService);
  private readonly confirmationService = inject(ConfirmationService);
  private readonly messageService = inject(MessageService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly sanitizer = inject(DomSanitizer);
  private readonly markdown = new MarkdownIt({ html: false, linkify: true, typographer: true, breaks: true });

  readonly info = signal<SystemUpdateInfoDto | null>(null);
  readonly releaseNotesHtml = computed(() => {
    const body = this.info()?.releaseInfo?.body;
    if (!body) return '';
    return this.sanitizer.sanitize(SecurityContext.HTML, this.markdown.render(body)) ?? '';
  });
  readonly loading = signal(false);
  readonly updating = signal(false);
  readonly restarting = signal(false);
  readonly waitingForRestart = signal(false);

  ngOnInit(): void {
    this.refresh(false);
  }

  refresh(force = true): void {
    this.loading.set(true);
    this.service
      .checkUpdates(force)
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        finalize(() => this.loading.set(false))
      )
      .subscribe(info => this.info.set(info));
  }

  confirmUpdate(): void {
    const current = this.info();
    if (!current?.hasUpdate || !current.updateSupported) {
      return;
    }

    this.confirmationService.confirm({
      header: '立即更新',
      message: `将从 ${current.currentVersion} 更新到 ${current.latestVersion}。更新过程中服务会短暂中断，确认继续？`,
      icon: 'pi pi-download',
      acceptLabel: '立即更新',
      rejectLabel: '取消',
      accept: () => this.runUpdate()
    });
  }

  confirmRestart(): void {
    this.confirmationService.confirm({
      header: '立即重启',
      message: '进程将退出并由 systemd / 容器策略拉起。进行中的请求会中断，确认重启？',
      icon: 'pi pi-refresh',
      acceptLabel: '立即重启',
      rejectLabel: '取消',
      accept: () => this.runRestart()
    });
  }

  runtimeLabel(runtime: string | undefined): string {
    return runtime === 'docker' ? '容器' : '进程';
  }

  runtimeSeverity(runtime: string | undefined): 'info' | 'success' {
    return runtime === 'docker' ? 'info' : 'success';
  }

  private runUpdate(): void {
    this.updating.set(true);
    this.service
      .performUpdate()
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        finalize(() => this.updating.set(false))
      )
      .subscribe({
        next: result => {
          this.messageService.add({
            severity: result.alreadyUpToDate ? 'info' : 'success',
            summary: result.alreadyUpToDate ? '已是最新' : '更新完成',
            detail: result.message
          });
          if (result.needRestart || result.recreateContainer) {
            this.waitUntilBack(this.info()?.latestVersion);
          } else {
            this.refresh(true);
          }
        },
        error: err => {
          if (err?.status === 0 || err?.status >= 502) {
            this.waitUntilBack();
          }
        }
      });
  }

  private runRestart(): void {
    this.restarting.set(true);
    this.service
      .restart()
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        finalize(() => this.restarting.set(false))
      )
      .subscribe({
        next: result => {
          this.messageService.add({ severity: 'success', summary: '正在重启', detail: result.message });
          this.waitUntilBack();
        },
        error: () => this.waitUntilBack()
      });
  }

  private waitUntilBack(expectedVersion?: string): void {
    this.waitingForRestart.set(true);
    timer(6000, 3000)
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        take(80),
        switchMap(() => this.service.getVersion().pipe(catchError(() => of(null)))),
        filter((version): version is SystemVersionDto => {
          if (!version?.version) {
            return false;
          }
          if (expectedVersion) {
            return version.version === expectedVersion;
          }
          return true;
        }),
        take(1),
        finalize(() => this.waitingForRestart.set(false))
      )
      .subscribe(version => {
        this.messageService.add({
          severity: 'success',
          summary: '服务已恢复',
          detail: `当前版本 ${version.version}`
        });
        this.refresh(true);
      });
  }
}
