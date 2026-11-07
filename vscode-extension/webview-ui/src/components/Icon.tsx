export function Icon({ name, className = '', label }: { name: string; className?: string; label?: string }) {
  return <span className={`codicon codicon-${name} ${className}`} aria-hidden={label ? undefined : true} aria-label={label} role={label ? 'img' : undefined} />;
}

export function OrbitLogo({ size = 16, className = '' }: { size?: number; className?: string }) {
  return (
    <svg className={className} width={size} height={size} viewBox="0 0 100 100" fill="none" stroke="currentColor" aria-hidden="true">
      <circle cx="50" cy="50" r="30" strokeWidth="8" />
      <circle cx="71.2" cy="28.8" r="9" fill="currentColor" stroke="none" />
    </svg>
  );
}
