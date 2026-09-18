import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ConfirmationService, MessageService } from 'primeng/api';
import { ButtonModule } from 'primeng/button';
import { CardModule } from 'primeng/card';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { TagModule } from 'primeng/tag';
import { catchError, filter, finalize, of, switchMap, take, timer } from 'rxjs';

import { SystemUpdateInfoDto, SystemVersionDto } from '../../models/system-update.dto';
import { SystemUpdateService } from '../../services/system-update-service';

@Component({
  selector: 'app-settings',
  standalone: true,
  imports: [CommonModule, CardModule, ButtonModule, TagModule, ConfirmDialogModule],
  providers: [ConfirmationService],
  templateUrl: './settings.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class Settings implements OnInit {
  private readonly service = inject(SystemUpdateService);
  private readonly confirmationService = inject(ConfirmationService);
  private readonly messageService = inject(MessageService);
  private readonly destroyRef = inject(DestroyRef);

  readonly info = signal<SystemUpdateInfoDto | null>(null);
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
            this.waitUntilBack();
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

  private waitUntilBack(): void {
    this.waitingForRestart.set(true);
    timer(4000, 2000)
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        take(90),
        switchMap(() => this.service.getVersion().pipe(catchError(() => of(null)))),
        filter((version): version is SystemVersionDto => version != null),
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
