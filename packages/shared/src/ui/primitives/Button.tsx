import { Slot } from '@radix-ui/react-slot';
import { cva, type VariantProps } from 'class-variance-authority';
import type { ComponentProps } from 'react';
import { cn } from '../cn';

/**
 * Variants, in the shadcn shape (cva) so a screen picks an intent rather than assembling
 * classes — which is how two buttons end up subtly different for no reason.
 *
 * Sizes honour the design system's 44px minimum touch target for the default and large
 * sizes; `sm` exists for dense staff tables, where the pointer is a mouse and the rows are
 * many (Z2: the portal is the showcase, the console is utilitarian).
 */
const buttonVariants = cva(
  'inline-flex items-center justify-center gap-2 rounded-sm font-semibold whitespace-nowrap transition-colors duration-100 disabled:pointer-events-none disabled:opacity-50',
  {
    variants: {
      variant: {
        primary: 'bg-primary text-on-primary hover:bg-primary-strong',
        secondary: 'border border-line bg-surface text-body hover:bg-surface-raised',
        ghost: 'text-primary hover:bg-primary-subtle',
        danger: 'bg-error text-on-primary hover:opacity-90',
      },
      size: {
        sm: 'h-8 px-3 text-sm',
        md: 'h-11 px-4 text-base',
        lg: 'h-12 px-6 text-lg',
      },
    },
    defaultVariants: {
      variant: 'primary',
      size: 'md',
    },
  },
);

export type ButtonProps = ComponentProps<'button'> &
  VariantProps<typeof buttonVariants> & {
    /**
     * Renders the child element instead of a `<button>`, keeping the styling.
     *
     * The one place this project needs Radix's Slot: a router `<Link>` that looks like a
     * button must stay an anchor, or it loses middle-click, open-in-new-tab, and the
     * meaning a screen reader announces.
     */
    asChild?: boolean;
  };

export function Button({ className, variant, size, asChild = false, ...props }: ButtonProps) {
  const Component = asChild ? Slot : 'button';

  return <Component className={cn(buttonVariants({ variant, size }), className)} {...props} />;
}

export { buttonVariants };
