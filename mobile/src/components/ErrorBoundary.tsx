import React from 'react';

interface ErrorBoundaryState {
  hasError: boolean;
}

export class ErrorBoundary extends React.Component<React.PropsWithChildren, ErrorBoundaryState> {
  public constructor(props: React.PropsWithChildren) {
    super(props);
    this.state = { hasError: false };
  }

  public static getDerivedStateFromError(): ErrorBoundaryState {
    return { hasError: true };
  }

  public componentDidCatch(error: Error): void {
    console.error('Fluentia mobile crashed:', error);
  }

  public render(): React.ReactNode {
    if (!this.state.hasError) {
      return this.props.children;
    }

    return (
      <div style={{
        minHeight: '100%',
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        padding: 24,
        background: '#0b1016',
        color: '#f5f5f7',
      }}>
        <div style={{
          width: '100%',
          maxWidth: 360,
          padding: 24,
          borderRadius: 24,
          background: 'rgba(28, 28, 30, 0.92)',
          border: '1px solid rgba(255, 255, 255, 0.08)',
          boxShadow: '0 22px 48px rgba(0, 0, 0, 0.28)',
        }}>
          <h1 style={{ fontSize: 20, fontWeight: 700 }}>Fluentia</h1>
          <p style={{ fontSize: 14, lineHeight: 1.6, marginTop: 12, color: '#c7c7cc' }}>
            应用刚刚遇到异常。请重新打开，或点下面的按钮重新加载。
          </p>
          <button
            type="button"
            onClick={() => window.location.reload()}
            style={{
              marginTop: 18,
              width: '100%',
              border: 'none',
              borderRadius: 14,
              padding: '12px 16px',
              background: '#0a84ff',
              color: '#ffffff',
              fontSize: 15,
              fontWeight: 600,
            }}
          >
            重新加载
          </button>
        </div>
      </div>
    );
  }
}