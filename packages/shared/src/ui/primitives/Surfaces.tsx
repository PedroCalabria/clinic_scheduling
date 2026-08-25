import { cva, type VariantProps } from 'class-variance-authority';
import type { ComponentProps } from 'react';
import { cn } from '../cn';

/** A panel. One elevation level exists in the design system, and cards do not use it. */
export function Card({ className, ...props }: ComponentProps<'div'>) {
  return (
    <div
      className={cn('rounded-md border border-line bg-surface-raised p-5', className)}
      {...props}
    />
  );
}

export function CardHeader({ className, ...props }: ComponentProps<'div'>) {
  return <div className={cn('mb-4 space-y-1', className)} {...props} />;
}

export function CardTitle({ className, ...props }: ComponentProps<'h2'>) {
  return <h2 className={cn('text-lg font-semibold text-heading', className)} {...props} />;
}

export function CardDescription({ className, ...props }: ComponentProps<'p'>) {
  return <p className={cn('text-sm text-meta', className)} {...props} />;
}

/**
 * `success` no longer fills with `primary-subtle`, and that is the point.
 *
 * The design system reserves `#DEF4F0` for one thing — a bookable slot — and `booking-surface`
 * found it filling this alert, the `active` badge below, and no slot at all. It fixed the slots and
 * left these two, recording the revisit trigger as "they serve the staff console too": re-pigmenting
 * a shared primitive to satisfy a rule about the patient portal would have been a larger change
 * wearing a smaller one's clothes.
 *
 * `booking-desk` brought three staff screens, so the trigger fired and this is the answer. The
 * border already carried the semantic; the fill was the borrowed part, so only the fill changed.
 */
const alertVariants = cva('rounded-md border p-4 text-sm', {
  variants: {
    tone: {
      error: 'border-error bg-tertiary-subtle/40 text-error',
      warning: 'border-tertiary bg-tertiary-subtle text-body',
      info: 'border-line bg-surface-raised text-body',
      success: 'border-primary bg-surface-raised text-primary-strong',
    },
  },
  defaultVariants: { tone: 'info' },
});

export type AlertProps = ComponentProps<'div'> & VariantProps<typeof alertVariants>;

/**
 * An inline message.
 *
 * Inline rather than a toast: these four screens report the outcome of something the user
 * just did, next to where they did it. A toast would move that message away from its cause
 * and take it off the screen on a timer, which is worse for everyone and actively bad for a
 * screen-reader user. `role="alert"` is set for the two tones that report a problem, so it
 * is announced without stealing focus.
 */
export function Alert({ className, tone, ...props }: AlertProps) {
  return (
    <div
      role={tone === 'error' || tone === 'warning' ? 'alert' : undefined}
      className={cn(alertVariants({ tone }), className)}
      {...props}
    />
  );
}

/** A data table. The staff console's main idiom (Z2). */
export function Table({ className, ...props }: ComponentProps<'table'>) {
  return (
    <div className="overflow-x-auto rounded-md border border-line">
      <table className={cn('w-full border-collapse text-left text-sm', className)} {...props} />
    </div>
  );
}

export function TableHead({ className, ...props }: ComponentProps<'thead'>) {
  return <thead className={cn('bg-surface-raised', className)} {...props} />;
}

export function TableRow({ className, ...props }: ComponentProps<'tr'>) {
  return <tr className={cn('border-b border-line last:border-0', className)} {...props} />;
}

export function TableHeaderCell({ className, ...props }: ComponentProps<'th'>) {
  return <th scope="col" className={cn('px-3 py-2 font-semibold text-heading', className)} {...props} />;
}

export function TableCell({ className, ...props }: ComponentProps<'td'>) {
  return <td className={cn('px-3 py-2 align-middle', className)} {...props} />;
}

/**
 * `active` becomes an outline rather than a fill, for the reason above — and it is an outline
 * rather than a paler fill for a second reason.
 *
 * `bg-surface-raised` would have made `active` and `neutral` differ by text colour alone, which is
 * exactly what the note on {@link Badge} forbids. A border is a second channel, so the state
 * survives being read by somebody who cannot distinguish the two greens.
 */
const badgeVariants = cva(
  'inline-flex items-center rounded-sm border border-transparent px-2 py-0.5 text-xs font-semibold',
  {
    variants: {
      tone: {
        neutral: 'bg-surface-raised text-meta',
        active: 'border-primary text-primary-strong',
        pending: 'bg-tertiary-subtle text-body',
        off: 'bg-surface-raised text-error',
      },
    },
    defaultVariants: { tone: 'neutral' },
  },
);

export type BadgeProps = ComponentProps<'span'> & VariantProps<typeof badgeVariants>;

/**
 * A status pill.
 *
 * Carries a text label always, never colour alone — colour is not information a
 * colour-blind or screen-reader user receives (WCAG 2.1 AA).
 */
export function Badge({ className, tone, ...props }: BadgeProps) {
  return <span className={cn(badgeVariants({ tone }), className)} {...props} />;
}
