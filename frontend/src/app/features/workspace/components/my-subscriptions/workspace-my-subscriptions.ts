import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ConfirmationService, MessageService } from 'primeng/api';
import { ButtonModule } from 'primeng/button';
import { CardModule } from 'primeng/card';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { TagModule } from 'primeng/tag';
import { TooltipModule } from 'primeng/tooltip';
import { finalize } from 'rxjs/operators';

import { LayoutService } from '../../../../layout/services/layout-service';
import { formatTokenCount } from '../../../../shared/utils/format.utils';
import { SubscriptionEditDialogComponent } from '../../../platform/components/subscriptions/widgets/subscription-edit-dialog/subscription-edit-dialog';
import { ApiKeyOutputDto, CreateApiKeyInputDto, UpdateApiKeyInputDto } from '../../../platform/models/subscription.dto';
import { SubscriptionService } from '../../../platform/services/subscription-service';

@Component({
  selector: 'app-workspace-my-subscriptions',
  standalone: true,
  imports: [
    CommonModule,
    ButtonModule,
    CardModule,
    TagModule,
    TooltipModule,
    ConfirmDialogModule,
    SubscriptionEditDialogComponent
  ],
  providers: [ConfirmationService],
  templateUrl: './workspace-my-subscriptions.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class WorkspaceMySubscriptionsPage {
  private readonly layoutService = inject(LayoutService);
  private readonly subscriptionService = inject(SubscriptionService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly messageService = inject(MessageService);
  private readonly confirmationService = inject(ConfirmationService);

  readonly loading = signal(true);
  readonly subscriptions = signal<ApiKeyOutputDto[]>([]);
  readonly visibleSecrets = signal<Record<string, boolean>>({});

  readonly dialogVisible = signal(false);
  readonly selectedSubscription = signal<ApiKeyOutputDto | null>(null);
  readonly saving = signal(false);

  constructor() {
    this.layoutService.title.set('我的订阅');
    this.reload();
  }

  reload() {
    this.loading.set(true);
    this.subscriptionService
      .getSubscriptions({ limit: 100, sorting: 'creationTime desc', onlyCurrentUser: true })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(data => {
        this.subscriptions.set(data.items);
        this.loading.set(false);
      });
  }

  openCreateDialog() {
    this.selectedSubscription.set(null);
    this.dialogVisible.set(true);
  }

  openEditDialog(item: ApiKeyOutputDto) {
    this.selectedSubscription.set(item);
    this.dialogVisible.set(true);
  }

  onSaveSubscription(payload: CreateApiKeyInputDto | UpdateApiKeyInputDto) {
    this.saving.set(true);
    const selected = this.selectedSubscription();
    const request$ = selected
      ? this.subscriptionService.updateSubscription(selected.id, payload)
      : this.subscriptionService.createSubscription(payload as CreateApiKeyInputDto);

    request$
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        finalize(() => this.saving.set(false))
      )
      .subscribe({
        next: () => {
          this.messageService.add({
            severity: 'success',
            summary: '成功',
            detail: selected ? '凭证已更新' : '订阅已创建'
          });
          this.dialogVisible.set(false);
          this.reload();
        }
      });
  }

  confirmDelete(item: ApiKeyOutputDto) {
    this.confirmationService.confirm({
      header: '确认删除',
      icon: 'pi pi-exclamation-triangle',
      message: `删除「${item.name}」后，使用此 Key 的应用会立刻失效，确定删除？`,
      acceptLabel: '删除',
      rejectLabel: '取消',
      acceptButtonStyleClass: 'p-button-danger',
      accept: () => {
        this.subscriptionService
          .deleteSubscription(item.id)
          .pipe(takeUntilDestroyed(this.destroyRef))
          .subscribe(() => {
            this.messageService.add({ severity: 'success', summary: '成功', detail: '凭证已删除' });
            this.reload();
          });
      }
    });
  }

  confirmToggleStatus(item: ApiKeyOutputDto) {
    if (item.isActive) {
      this.confirmationService.confirm({
        header: '确认停用',
        icon: 'pi pi-exclamation-triangle',
        message: `停用「${item.name}」后，使用此 Key 的请求会被拒绝，确定停用？`,
        acceptLabel: '停用',
        rejectLabel: '取消',
        accept: () => this.toggleStatus(item, false)
      });
      return;
    }

    this.toggleStatus(item, true);
  }

  private toggleStatus(item: ApiKeyOutputDto, isActive: boolean) {
    this.subscriptionService
      .toggleStatus(item.id, isActive)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => {
        this.messageService.add({
          severity: 'success',
          summary: '成功',
          detail: isActive ? '凭证已启用' : '凭证已停用'
        });
        this.reload();
      });
  }

  toggleSecret(id: string) {
    this.visibleSecrets.update(state => ({ ...state, [id]: !state[id] }));
  }

  getSecret(item: ApiKeyOutputDto) {
    if (this.visibleSecrets()[item.id]) {
      return item.secret;
    }

    if (!item.secret || item.secret.length < 12) {
      return '***';
    }

    return `${item.secret.slice(0, 7)}...${item.secret.slice(-4)}`;
  }

  copySecret(secret: string) {
    navigator.clipboard.writeText(secret).then(() => {
      this.messageService.add({ severity: 'success', summary: '成功', detail: '密钥已复制', life: 2000 });
    });
  }

  getStatusSeverity(item: ApiKeyOutputDto) {
    if (!item.isActive) return 'contrast';
    if (this.getExpiryState(item) === 'expired') return 'danger';
    return 'success';
  }
  
  getStatusLabel(item: ApiKeyOutputDto) {
    if (!item.isActive) return '停用';
    if (this.getExpiryState(item) === 'expired') return '已过期';
    return '启用';
  }

  getExpiryState(item: ApiKeyOutputDto): 'expired' | 'warning' | 'ok' | 'forever' {
    if (!item.expiresAt) return 'forever';

    const now = new Date().getTime();
    const expiry = new Date(item.expiresAt).getTime();
    const diff = expiry - now;

    if (diff <= 0) return 'expired';
    if (diff < 3 * 24 * 3600 * 1000) return 'warning';
    return 'ok';
  }

  formatExpiresAt(value?: string) {
    return value ? new Date(value).toLocaleString('zh-CN') : '永不过期';
  }

  formatTokenCount = formatTokenCount;
}
