import { Badge, Button, TableCell } from '@clinic/shared';
import { useTranslation } from 'react-i18next';

/**
 * The pieces all four catalog tables share.
 *
 * App-local composition, not an abstraction over the catalog itself: the design deliberately
 * keeps the four entity kinds as four explicit slices (decision D4) because their rules
 * differ. What does not differ is how a row reports whether it is offered and how it is
 * retired or restored, so that lives here once rather than four times.
 */

/** Whether a record is offered, as a label and not only a colour (WCAG 2.1 AA). */
export function StatusCell({ isActive }: { isActive: boolean }) {
  const { t } = useTranslation();

  return (
    <TableCell>
      <Badge tone={isActive ? 'active' : 'off'}>
        {isActive ? t('catalog.statusActive') : t('catalog.statusInactive')}
      </Badge>
    </TableCell>
  );
}

/**
 * Edit, plus retire or restore.
 *
 * Both directions are always offered, because retirement is reversible (design D1) and a
 * one-way button on a list of things an administrator mis-clicked would be a support ticket.
 */
export function ActionsCell({
  isActive,
  onEdit,
  onToggle,
  busy,
}: {
  isActive: boolean;
  onEdit: () => void;
  onToggle: () => void;
  busy: boolean;
}) {
  const { t } = useTranslation();

  return (
    <TableCell>
      <div className="flex gap-2">
        <Button variant="secondary" size="sm" onClick={onEdit} disabled={busy}>
          {t('catalog.edit')}
        </Button>
        <Button variant="secondary" size="sm" onClick={onToggle} disabled={busy}>
          {isActive ? t('catalog.deactivate') : t('catalog.reactivate')}
        </Button>
      </div>
    </TableCell>
  );
}

/** Page heading and its one-line explanation of what this screen is for. */
export function CatalogHeading({ title, description }: { title: string; description: string }) {
  return (
    <div>
      <h1 className="text-2xl font-semibold text-heading">{title}</h1>
      <p className="mt-1 text-meta">{description}</p>
    </div>
  );
}
