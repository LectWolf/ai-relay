export interface SystemVersionDto {
  version: string;
  customVersion: string;
  upstreamVersion: string;
  runtime: 'process' | 'docker' | string;
  updateSupported: boolean;
}

export interface ReleaseInfoDto {
  name: string;
  body: string;
  publishedAt: string;
  htmlUrl: string;
}

export interface SystemUpdateInfoDto {
  currentVersion: string;
  latestVersion: string;
  upstreamVersion: string;
  hasUpdate: boolean;
  runtime: 'process' | 'docker' | string;
  updateSupported: boolean;
  warning?: string | null;
  releaseInfo?: ReleaseInfoDto | null;
}

export interface SystemUpdateResultDto {
  message: string;
  needRestart: boolean;
  recreateContainer: boolean;
  targetImage?: string | null;
  alreadyUpToDate: boolean;
  runtime: string;
}

export interface SystemRestartResultDto {
  message: string;
}
