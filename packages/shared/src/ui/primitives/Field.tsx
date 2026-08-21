import type { ComponentProps, ReactNode } from 'react';
import { useId } from 'react';
import { cn } from '../cn';

/**
 * A text input.
 *
 * A plain `<input>` rather than a Radix wrapper: Radix earns its place on widgets the
 * platform does not provide accessibly (dialogs, comboboxes, popovers), and an input is not
 * one of them. `components.json` is configured so the shadcn CLI drops those into this
 * package when a screen genuinely needs one.
 */
export function Input({ className, ...props }: ComponentProps<'input'>) {
  return (
    <input
      className={cn(
        'h-11 w-full rounded-sm border border-line bg-surface px-3 text-base text-body',
        'placeholder:text-meta disabled:opacity-50',
        'aria-[invalid=true]:border-error',
        className,
      )}
      {...props}
    />
  );
}

/** A styled native select, for the same reason as {@link Input}. */
export function Select({ className, children, ...props }: ComponentProps<'select'>) {
  return (
    <select
      className={cn(
        'h-11 w-full rounded-sm border border-line bg-surface px-3 text-base text-body disabled:opacity-50',
        className,
      )}
      {...props}
    >
      {children}
    </select>
  );
}

export function Label({ className, ...props }: ComponentProps<'label'>) {
  return <label className={cn('text-sm font-semibold text-body', className)} {...props} />;
}

export interface FieldProps {
  label: string;
  /** Rendered beneath the control, and announced with it. */
  hint?: ReactNode;
  /** Translated message. Its presence is what marks the control invalid. */
  error?: ReactNode;
  /** Receives the generated ids, so the label and messages are actually associated. */
  children: (ids: { id: string; describedBy: string | undefined; invalid: boolean }) => ReactNode;
}

/**
 * Label, control, hint, and error, wired together.
 *
 * The wiring is the point. A label needs `htmlFor`, an error needs to be in
 * `aria-describedby` and to set `aria-invalid`, and a screen reader user who cannot see the
 * red border gets nothing without them. Doing it per form is how one form ends up missing
 * it, so the ids are generated here and handed to the control.
 */
export function Field({ label, hint, error, children }: FieldProps) {
  const id = useId();
  const hintId = `${id}-hint`;
  const errorId = `${id}-error`;

  const describedBy = [hint ? hintId : null, error ? errorId : null].filter(Boolean).join(' ') || undefined;

  return (
    <div className="space-y-1.5">
      <Label htmlFor={id}>{label}</Label>
      {children({ id, describedBy, invalid: Boolean(error) })}
      {hint ? (
        <p id={hintId} className="text-sm text-meta">
          {hint}
        </p>
      ) : null}
      {error ? (
        <p id={errorId} className="text-sm font-semibold text-error">
          {error}
        </p>
      ) : null}
    </div>
  );
}
