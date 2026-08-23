import {
  Alert,
  Badge,
  Button,
  Dialog,
  DialogContent,
  DialogFooter,
  Field,
  Input,
  Table,
  TableCell,
  TableHead,
  TableHeaderCell,
  TableRow,
  createMyBlock,
  listMyBlocks,
  setMyBlockActive,
  updateMyBlock,
  useApiErrorMessage,
  type TimeBlockResponse,
} from '@clinic/shared';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';

export const BLOCKS_QUERY_KEY = ['blocks'] as const;

/**
 * S3 — a professional's own blocked time (docs/06-ui-surfaces.md).
 *
 * The first professional-role screen in this console: every screen before it was for an
 * administrator. It is also the producer that makes change 4's availability subtraction
 * demonstrable — block an hour here and those times stop being offered.
 *
 * The shape follows the catalog screens (a table plus the dialog primitive) rather than
 * inventing a calendar. A block is two instants and a state; a week grid would have nowhere to
 * put a period spanning days, which is the same reason 3b chose a table for working hours.
 */
export function BlockedTimePage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const describeError = useApiErrorMessage();

  const { data, isPending, isError, error } = useQuery({
    queryKey: BLOCKS_QUERY_KEY,
    queryFn: listMyBlocks,
    retry: false,
  });

  /** `null` means closed; a record means editing it; `'new'` means creating. */
  const [editing, setEditing] = useState<TimeBlockResponse | 'new' | null>(null);
  const [startsAt, setStartsAt] = useState('');
  const [endsAt, setEndsAt] = useState('');
  const [notice, setNotice] = useState<string | null>(null);

  const invalidate = () => queryClient.invalidateQueries({ queryKey: BLOCKS_QUERY_KEY });

  const save = useMutation({
    mutationFn: () =>
      editing && editing !== 'new'
        ? updateMyBlock(editing.id, { startsAt, endsAt })
        : createMyBlock({ startsAt, endsAt }),
    onSuccess: () => {
      setNotice(editing === 'new' ? t('blocks.created') : t('blocks.updated'));
      setEditing(null);
      void invalidate();
    },
  });

  const toggle = useMutation({
    mutationFn: (block: TimeBlockResponse) => setMyBlockActive(block.id, !block.isActive),
    onSuccess: (_result, block) => {
      setNotice(block.isActive ? t('blocks.retired') : t('blocks.restored'));
      void invalidate();
    },
  });

  function open(target: TimeBlockResponse | 'new') {
    setNotice(null);
    save.reset();
    setStartsAt(target === 'new' ? '' : target.startsAt);
    setEndsAt(target === 'new' ? '' : target.endsAt);
    setEditing(target);
  }

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-2xl font-semibold text-heading">{t('blocks.title')}</h1>
        <p className="mt-1 text-meta">{t('blocks.description')}</p>
      </div>

      {notice ? <Alert tone="success">{notice}</Alert> : null}
      {/* A refused retire reports next to the table it acted on, never as a toast. */}
      {toggle.isError ? <Alert tone="error">{describeError(toggle.error)}</Alert> : null}

      <Button onClick={() => open('new')}>{t('blocks.add')}</Button>

      {isPending ? (
        <p role="status" className="text-meta">
          {t('common.loading')}
        </p>
      ) : isError ? (
        <Alert tone="error">{describeError(error)}</Alert>
      ) : data && data.blocks.length > 0 ? (
        <>
          {/*
            Which clock these times are on, said out loud. A professional in another timezone
            would otherwise reasonably read them as their own, and the clinic has exactly one
            (Decision H).
          */}
          <p className="text-sm text-meta">{t('blocks.timezoneNote', { timezone: data.timezone })}</p>

          <Table>
            <TableHead>
              <TableRow>
                <TableHeaderCell>{t('blocks.columnFrom')}</TableHeaderCell>
                <TableHeaderCell>{t('blocks.columnTo')}</TableHeaderCell>
                <TableHeaderCell>{t('blocks.columnState')}</TableHeaderCell>
                <TableHeaderCell>{t('blocks.columnActions')}</TableHeaderCell>
              </TableRow>
            </TableHead>
            <tbody>
              {data.blocks.map((block) => (
                <TableRow key={block.id}>
                  <TableCell className="font-medium">{formatWallClock(block.startsAt)}</TableCell>
                  <TableCell>{formatWallClock(block.endsAt)}</TableCell>
                  <TableCell>
                    {/* A label, not only a colour (WCAG 2.1 AA). */}
                    <Badge tone={block.isActive ? 'active' : 'off'}>
                      {block.isActive ? t('blocks.stateActive') : t('blocks.stateRetired')}
                    </Badge>
                  </TableCell>
                  <TableCell>
                    <div className="flex gap-2">
                      <Button
                        variant="secondary"
                        size="sm"
                        onClick={() => open(block)}
                        disabled={toggle.isPending}
                      >
                        {t('blocks.edit')}
                      </Button>
                      {/*
                        Both directions always offered, because retirement is reversible
                        (design D1) and a one-way button on a mis-clicked row is a support
                        ticket.
                      */}
                      <Button
                        variant="secondary"
                        size="sm"
                        onClick={() => {
                          setNotice(null);
                          toggle.mutate(block);
                        }}
                        disabled={toggle.isPending}
                      >
                        {block.isActive ? t('blocks.retire') : t('blocks.restore')}
                      </Button>
                    </div>
                  </TableCell>
                </TableRow>
              ))}
            </tbody>
          </Table>
        </>
      ) : (
        <p className="text-meta">{t('blocks.empty')}</p>
      )}

      <Dialog open={editing !== null} onOpenChange={(next) => !next && setEditing(null)}>
        {editing !== null ? (
          <DialogContent title={editing === 'new' ? t('blocks.add') : t('blocks.edit')}>
            <form
              className="space-y-5"
              onSubmit={(event) => {
                event.preventDefault();
                save.mutate();
              }}
            >
              {/*
                `datetime-local` rather than a wrapped widget: it collects a wall-clock time with
                no zone attached, which is exactly what the server interprets against the clinic's
                configured zone. A control that volunteered an offset would be the wrong tool.
              */}
              <Field label={t('blocks.from')}>
                {({ id, describedBy, invalid }) => (
                  <Input
                    id={id}
                    type="datetime-local"
                    aria-describedby={describedBy}
                    aria-invalid={invalid}
                    value={startsAt}
                    onChange={(event) => setStartsAt(event.target.value)}
                    required
                    autoFocus
                  />
                )}
              </Field>

              <Field label={t('blocks.to')}>
                {({ id, describedBy, invalid }) => (
                  <Input
                    id={id}
                    type="datetime-local"
                    aria-describedby={describedBy}
                    aria-invalid={invalid}
                    value={endsAt}
                    onChange={(event) => setEndsAt(event.target.value)}
                    required
                  />
                )}
              </Field>

              {/* Inside the dialog, above the buttons: where the eye already is. */}
              {save.isError ? <Alert tone="error">{describeError(save.error)}</Alert> : null}

              <DialogFooter>
                <Button type="button" variant="secondary" onClick={() => setEditing(null)}>
                  {t('common.cancel')}
                </Button>
                <Button type="submit" disabled={save.isPending}>
                  {t('common.save')}
                </Button>
              </DialogFooter>
            </form>
          </DialogContent>
        ) : null}
      </Dialog>
    </div>
  );
}

/**
 * Renders the API's wall-clock string for reading.
 *
 * Deliberately not `new Date(value)`. That would parse the string in the BROWSER's timezone and
 * then display it shifted — the exact bug this whole change stores instants to avoid,
 * reintroduced at the last step. The server already expressed the time in the clinic's zone, so
 * the only correct thing to do is show it.
 */
function formatWallClock(value: string): string {
  return value.replace('T', ' ');
}
