import React, { useMemo, useState } from 'react';
import {
  CheckIcon,
  CloseIcon,
  CollapseIcon,
  ExpandIcon,
  PauseIcon,
  ResendIcon,
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
  const showSuccess = batch.status === 'completed';

  return (
    <div className={`transfer-card ${batch.status === 'completed' ? 'is-complete' : ''} ${batch.status === 'paused' ? 'is-paused' : ''}`}>
      <div className="transfer-card-inner">
        <div className="transfer-copy">
          <div className="transfer-headline-row">
            <div className="transfer-title">{title}</div>
            <div className="transfer-percent-pill">{summary.percent}%</div>
          </div>
          <div className="transfer-subtitle">{showSuccess ? 'Completed' : summary.secondaryText}</div>
          <div className="transfer-progress-track" aria-hidden="true">
            <div
              className="transfer-progress-line"
              style={{ width: `${summary.percent}%` }}
            />
          </div>
        </div>

        <div className="transfer-action-rail" aria-hidden={!showExpand && !showPause && !showCancel}>
          <div className="transfer-actions">
            {showPause && (
              <button type="button" className={`transfer-action-btn transfer-action-morph ${batch.status === 'paused' ? 'is-paused' : ''}`} onClick={onPauseToggle} aria-label={batch.status === 'paused' ? 'Resume transfer' : 'Pause transfer'}>
                <span className="transfer-morph-icon" aria-hidden="true">
                  <span className="transfer-morph-layer pause-layer">
                    <PauseIcon size={16} />
                  </span>
                  <span className="transfer-morph-layer resume-layer">
                    <ResendIcon size={14} />
                  </span>
                </span>
              </button>
            )}
            {showCancel && (
              <button type="button" className="transfer-action-btn" onClick={onCancel} aria-label="Cancel transfer">
                <CloseIcon size={14} />
              </button>
            )}
            {showExpand && (
              <button type="button" className="transfer-action-btn icon-plain" onClick={() => setExpanded((value) => !value)} aria-label={expanded ? 'Collapse transfer details' : 'Expand transfer details'}>
                {expanded ? <CollapseIcon size={15} /> : <ExpandIcon size={15} />}
              </button>
            )}
            {showSuccess && (
              <div className="transfer-success-mark" aria-hidden="true">
                <CheckIcon size={16} />
              </div>
            )}
          </div>
        </div>
      </div>

      <div className={`transfer-details-shell ${expanded && showExpand ? 'expanded' : ''}`}>
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
      </div>
    </div>
  );
};
