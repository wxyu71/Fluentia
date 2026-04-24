import React, { useMemo, useState } from 'react';
import {
  CheckIcon,
  CloseIcon,
  CollapseIcon,
  DownloadIcon,
  ExpandIcon,
  MoreIcon,
  PauseIcon,
  PlayIcon,
  UploadIcon,
} from './Icons';
import type { TransferBatchProgress, TransferFileProgress } from '../types';

interface TransferStatusCardProps {
  batch: TransferBatchProgress;
  onPauseToggle?: () => void;
  onCancel?: () => void;
}

function formatSecondsRemaining(batch: TransferBatchProgress, transferredBytes: number, totalBytes: number): string {
  if (batch.status === 'paused') {
    return 'Paused';
  }

  if (batch.status === 'cancelled') {
    return 'Cancelled';
  }

  if (batch.status === 'failed') {
    return 'Failed';
  }

  if (batch.status === 'completed') {
    return 'Completed';
  }

  if (transferredBytes <= 0 || totalBytes <= 0) {
    return 'Preparing';
  }

  const elapsedSeconds = Math.max((Date.now() - batch.startedAt) / 1000, 0.35);
  const bytesPerSecond = transferredBytes / elapsedSeconds;
  if (!Number.isFinite(bytesPerSecond) || bytesPerSecond <= 0) {
    return 'Preparing';
  }

  const remainingSeconds = Math.max(0, Math.round((totalBytes - transferredBytes) / bytesPerSecond));
  if (remainingSeconds <= 1) {
    return 'Finishing';
  }

  return `${remainingSeconds} seconds left`;
}

function formatPercent(file: TransferFileProgress): number {
  if (file.totalBytes <= 0) {
    return file.status === 'completed' ? 100 : 0;
  }

  return Math.max(0, Math.min(100, Math.round((file.transferredBytes / file.totalBytes) * 100)));
}

export const TransferStatusCard: React.FC<TransferStatusCardProps> = ({
  batch,
  onPauseToggle,
  onCancel,
}) => {
  const [expanded, setExpanded] = useState(false);

  const summary = useMemo(() => {
    const totalBytes = batch.files.reduce((sum, file) => sum + Math.max(0, file.totalBytes), 0);
    const transferredBytes = batch.files.reduce((sum, file) => sum + Math.max(0, file.transferredBytes), 0);
    const percent = totalBytes > 0
      ? Math.max(0, Math.min(100, Math.round((transferredBytes / totalBytes) * 100)))
      : batch.status === 'completed' ? 100 : 0;

    return {
      count: batch.files.length,
      totalBytes,
      transferredBytes,
      percent,
      secondaryText: formatSecondsRemaining(batch, transferredBytes, totalBytes),
    };
  }, [batch]);

  const title = batch.status === 'completed'
    ? `${batch.direction === 'upload' ? 'Uploaded' : 'Received'} ${summary.count} ${summary.count === 1 ? 'file' : 'files'}`
    : `${batch.direction === 'upload' ? 'Uploading' : 'Receiving'} ${summary.count} ${summary.count === 1 ? 'file' : 'files'}`;

  const showPause = typeof onPauseToggle === 'function' && (batch.status === 'active' || batch.status === 'paused');
  const showCancel = typeof onCancel === 'function' && (batch.status === 'active' || batch.status === 'paused');
  const showExpand = batch.files.length > 1;
  const showActions = batch.status !== 'completed';
  const showSuccess = batch.status === 'completed';

  return (
    <div className={`transfer-card ${batch.status === 'completed' ? 'is-complete' : ''} ${batch.status === 'paused' ? 'is-paused' : ''}`}>
      <div className="transfer-card-inner">
        <div className="transfer-copy">
          <div className="transfer-title-row">
            <span className={`transfer-direction-chip ${batch.direction}`}>
              {batch.direction === 'upload' ? <UploadIcon size={14} /> : <DownloadIcon size={14} />}
              {batch.direction === 'upload' ? 'Send' : 'Receive'}
            </span>
            <div className="transfer-title">{title}</div>
          </div>
          <div className="transfer-subtitle">
            {summary.percent}% · {summary.secondaryText}
          </div>
        </div>

        <div className="transfer-actions" aria-hidden={!showActions && !showExpand}>
          {showPause && (
            <button type="button" className="transfer-action-btn" onClick={onPauseToggle} aria-label={batch.status === 'paused' ? 'Resume transfer' : 'Pause transfer'}>
              {batch.status === 'paused' ? <PlayIcon size={16} /> : <PauseIcon size={16} />}
            </button>
          )}
          {showCancel && (
            <button type="button" className="transfer-action-btn" onClick={onCancel} aria-label="Cancel transfer">
              <CloseIcon size={14} />
            </button>
          )}
          {showExpand && (
            <button type="button" className="transfer-action-btn" onClick={() => setExpanded((value) => !value)} aria-label={expanded ? 'Collapse transfer details' : 'Expand transfer details'}>
              {expanded ? <CollapseIcon size={15} /> : <ExpandIcon size={15} />}
            </button>
          )}
          {showActions && (
            <button type="button" className="transfer-action-btn muted" aria-label="More options" disabled>
              <MoreIcon size={14} />
            </button>
          )}
          {showSuccess && (
            <div className="transfer-success-mark" aria-hidden="true">
              <CheckIcon size={16} />
            </div>
          )}
        </div>
      </div>

      <div className="transfer-progress-track" aria-hidden="true">
        <div
          className="transfer-progress-line"
          style={{ transform: `scaleX(${summary.percent / 100})` }}
        />
      </div>

      {expanded && showExpand && (
        <div className="transfer-details">
          {batch.files.map((file, index) => (
            <div
              key={file.id}
              className="transfer-file-row"
              style={{ animationDelay: `${index * 40}ms` }}
            >
              <div className="transfer-file-copy">
                <div className="transfer-file-name">{file.name}</div>
                <div className="transfer-file-meta">
                  {formatPercent(file)}% · {file.status === 'completed' ? 'Ready' : file.status === 'paused' ? 'Paused' : file.status === 'cancelled' ? 'Cancelled' : 'Transferring'}
                </div>
              </div>
              <div className="transfer-file-percent">{formatPercent(file)}%</div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
};
