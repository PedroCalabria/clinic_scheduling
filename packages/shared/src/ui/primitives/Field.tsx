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

export interface RadioOption {
  value: string;
  label: string;
  /** Rendered under the option's label. For the difference between two similar choices. */
  hint?: string;
}

export interface RadioGroupProps {
  /** The question the options answer. Becomes the group's `<legend>`. */
  label: string;
  options: RadioOption[];
  value: string;
  onChange: (value: string) => void;
  /** Rendered beneath the group and announced with it. */
  hint?: ReactNode;
  className?: string;
}

/**
 * A small set of mutually exclusive choices, all of them visible.
 *
 * <b>Why this is not a {@link Select}.</b> A select hides every option but one and implies a list
 * of interchangeable values. A radio group says "here are the two ways to do this" — which is the
 * distinction `booking-surface` needed on P2, where searching *any* qualified professional is the
 * primary booking mode (02 §4) and had been sitting as the first entry in a dropdown of names,
 * reading as an escape hatch rather than the main road.
 *
 * <b>Why it is native radios in a fieldset, and not a Radix widget or a hand-rolled one.</b> The
 * browser already implements the WAI-ARIA radio-group pattern for `<input type="radio">` elements
 * sharing a `name`: the group is one tab stop, arrow keys move the selection within it, and the
 * `<legend>` names the group. That is precisely the behaviour a hand-rolled pair of styled divs
 * gets wrong. Same argument the {@link Input} comment makes: Radix earns its place on widgets the
 * platform does not provide accessibly, and this is not one of them.
 *
 * <b>Why it does not go through {@link Field}.</b> `Field` renders `<label htmlFor>`, which is only
 * valid for a single labellable control — a group is labelled by its `<legend>`. Forcing this
 * through `Field` would produce an association that looks right in the markup and does nothing in
 * a screen reader, so the visual contract is mirrored and the semantics are not.
 */
export function RadioGroup({ label, options, value, onChange, hint, className }: RadioGroupProps) {
  const name = useId();
  const hintId = `${name}-hint`;

  return (
    <fieldset className={cn('space-y-1.5', className)} aria-describedby={hint ? hintId : undefined}>
      <legend className="text-sm font-semibold text-body">{label}</legend>

      <div className="space-y-1">
        {options.map((option) => (
          <label
            key={option.value}
            className="flex min-h-11 cursor-pointer items-start gap-2.5 py-1 text-base text-body"
          >
            <input
              type="radio"
              name={name}
              value={option.value}
              checked={value === option.value}
              onChange={() => onChange(option.value)}
              className="mt-1 size-4 shrink-0 accent-primary"
            />
            <span>
              {option.label}
              {option.hint ? <span className="block text-sm text-meta">{option.hint}</span> : null}
            </span>
          </label>
        ))}
      </div>

      {hint ? (
        <p id={hintId} className="text-sm text-meta">
          {hint}
        </p>
      ) : null}
    </fieldset>
  );
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
