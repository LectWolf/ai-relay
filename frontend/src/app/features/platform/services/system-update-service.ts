import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, timeout } from 'rxjs';

import {
  SystemRestartResultDto,
  SystemUpdateInfoDto,
  SystemUpdateResultDto,
  SystemVersionDto
} from '../models/system-update.dto';

@Injectable({ providedIn: 'root' })
export class SystemUpdateService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/api/v1/admin/system';
  private readonly longTimeout = 15 * 60 * 1000;

  getVersion(): Observable<SystemVersionDto> {
    return this.http.get<SystemVersionDto>(`${this.baseUrl}/version`);
  }

  checkUpdates(force = false): Observable<SystemUpdateInfoDto> {
    let params = new HttpParams();
    if (force) params = params.set('force', 'true');
    return this.http.get<SystemUpdateInfoDto>(`${this.baseUrl}/check-updates`, { params });
  }

  performUpdate(): Observable<SystemUpdateResultDto> {
    return this.http.post<SystemUpdateResultDto>(`${this.baseUrl}/update`, {}).pipe(timeout(this.longTimeout));
  }

  restart(): Observable<SystemRestartResultDto> {
    return this.http.post<SystemRestartResultDto>(`${this.baseUrl}/restart`, {});
  }
}
