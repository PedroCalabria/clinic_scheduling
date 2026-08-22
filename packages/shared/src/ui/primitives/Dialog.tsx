import * as DialogPrimitive from '@radix-ui/react-dialog';
import type { ComponentProps, ReactNode } from 'react';
import { cn } from '../cn';

/**
 * A modal form.
 *
 * The first widget in this project that Radix genuinely earns (00-context.md §2, design D7).
 * Everything before it — inputs, selects, tables — is a native element the platform already
 * makes accessible, and wrapping those would be ceremony. A dialog is different: it has to
 * trap focus, restore it to the trigger on close, close on Escape, mark the rest of the page
 * inert for assistive technology, and be labelled by its own title. That is a list this
 * project would get subtly wrong by hand, and getting it wrong is invisible until a
 * keyboard-only user is stuck behind an open form.
 *
 * The alternative considered was a route per form. Four entity kinds × create and edit is
 * eight routes for what is a three-field form, and the back button would become the cancel
 * button.
 *
 * The one deliberate style choice: this is the only place in the system that carries a
 * shadow, because it is the only thing genuinely floating above other content.
 */
export function Dialog(props: ComponentProps<typeof DialogPrimitive.Root>) {
  return <DialogPrimitive.Root {...props} />;
}

export function DialogTrigger(props: ComponentProps<typeof DialogPrimitive.Trigger>) {
  return <DialogPrimitive.Trigger {...props} />;
}

export function DialogClose(props: ComponentProps<typeof DialogPrimitive.Close>) {
  return <DialogPrimitive.Close {...props} />;
}

export interface DialogContentProps extends ComponentProps<typeof DialogPrimitive.Content> {
  /** Required: it labels the dialog for assistive technology, not merely the eye. */
  title: string;
  description?: ReactNode;
}

export function DialogContent({
  title,
  description,
  className,
  children,
  ...props
}: DialogContentProps) {
  return (
    <DialogPrimitive.Portal>
      <DialogPrimitive.Overlay className="fixed inset-0 z-40 bg-heading/40" />
      <DialogPrimitive.Content
        className={cn(
          'fixed left-1/2 top-1/2 z-50 w-[min(32rem,calc(100vw-2rem))]',
          '-translate-x-1/2 -translate-y-1/2',
          'max-h-[calc(100vh-2rem)] overflow-y-auto',
          'rounded-md border border-line bg-surface p-6 shadow-[0_4px_12px_rgba(27,35,36,0.12)]',
          className,
        )}
        {...props}
      >
        <div className="mb-4 space-y-1">
          <DialogPrimitive.Title className="text-lg font-semibold text-heading">
            {title}
          </DialogPrimitive.Title>
          {description ? (
            <DialogPrimitive.Description className="text-sm text-meta">
              {description}
            </DialogPrimitive.Description>
          ) : null}
        </div>

        {children}
      </DialogPrimitive.Content>
    </DialogPrimitive.Portal>
  );
}

/** Right-aligned action row — cancel then confirm, in reading order. */
export function DialogFooter({ className, ...props }: ComponentProps<'div'>) {
  return <div className={cn('mt-6 flex justify-end gap-3', className)} {...props} />;
}
