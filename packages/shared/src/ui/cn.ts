import { type ClassValue, clsx } from 'clsx';
import { twMerge } from 'tailwind-merge';

/**
 * Merges class names, letting a caller's utility win over a component's default.
 *
 * The shadcn convention, and it earns its place: without it, `<Button className="w-full">`
 * would emit two conflicting width classes and the outcome would depend on stylesheet
 * order rather than on what the caller asked for.
 */
export function cn(...inputs: ClassValue[]): string {
  return twMerge(clsx(inputs));
}
