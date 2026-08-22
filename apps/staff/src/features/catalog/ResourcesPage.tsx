import {
  Alert,
  Button,
  Card,
  CardDescription,
  CardHeader,
  CardTitle,
  Dialog,
  DialogContent,
  DialogFooter,
  Field,
  Input,
  Select,
  Table,
  TableCell,
  TableHead,
  TableHeaderCell,
  TableRow,
  createResource,
  createResourceType,
  listResourceTypes,
  listResources,
  setCatalogEntityActive,
  updateResource,
  updateResourceType,
  useApiErrorMessage,
  type ResourceResponse,
  type ResourceTypeResponse,
} from '@clinic/shared';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ActionsCell, CatalogHeading, StatusCell } from './CatalogBits';

const RESOURCE_TYPES_QUERY_KEY = ['config', 'resource-types'] as const;
const RESOURCES_QUERY_KEY = ['config', 'resources'] as const;

/**
 * S9 — Resources & types (docs/06-ui-surfaces.md).
 *
 * Two tables on one screen, by decision D6: they are two tables but one mental model ("what
 * rooms and equipment does this clinic have?"), and a resource cannot exist before its type
 * does. Splitting them would make the first thing an administrator does a navigation puzzle.
 *
 * The turnaround buffer is edited on the type, where it belongs, and is labelled as
 * turnaround rather than "buffer" — the person filling it in is thinking about cleaning a
 * room, not about an availability algorithm.
 */
export function ResourcesPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const describeError = useApiErrorMessage();

  const types = useQuery({
    queryKey: RESOURCE_TYPES_QUERY_KEY,
    queryFn: listResourceTypes,
    retry: false,
  });

  const resources = useQuery({
    queryKey: RESOURCES_QUERY_KEY,
    queryFn: listResources,
    retry: false,
  });

  const [notice, setNotice] = useState<string | null>(null);

  // Retiring a type changes what the resource table may reference, so both lists refresh.
  const invalidateAll = () => {
    void queryClient.invalidateQueries({ queryKey: RESOURCE_TYPES_QUERY_KEY });
    void queryClient.invalidateQueries({ queryKey: RESOURCES_QUERY_KEY });
  };

  const toggle = useMutation({
    mutationFn: (target: { collection: 'resource-types' | 'resources'; id: string; isActive: boolean }) =>
      setCatalogEntityActive(target.collection, target.id, !target.isActive),
    onSuccess: (_result, target) => {
      setNotice(target.isActive ? t('catalog.deactivated') : t('catalog.reactivated'));
      invalidateAll();
    },
  });

  const activeTypes = (types.data ?? []).filter((type) => type.isActive);

  return (
    <div className="space-y-6">
      <CatalogHeading
        title={t('catalog.resourcesTitle')}
        description={t('catalog.resourcesDescription')}
      />

      {notice ? <Alert tone="success">{notice}</Alert> : null}
      {toggle.isError ? <Alert tone="error">{describeError(toggle.error)}</Alert> : null}

      <ResourceTypeSection
        query={types}
        onChanged={(message) => {
          setNotice(message);
          invalidateAll();
        }}
        onToggle={(type) => {
          setNotice(null);
          toggle.mutate({ collection: 'resource-types', id: type.id, isActive: type.isActive });
        }}
        toggling={toggle.isPending}
      />

      <ResourceSection
        query={resources}
        activeTypes={activeTypes}
        onChanged={(message) => {
          setNotice(message);
          invalidateAll();
        }}
        onToggle={(resource) => {
          setNotice(null);
          toggle.mutate({ collection: 'resources', id: resource.id, isActive: resource.isActive });
        }}
        toggling={toggle.isPending}
      />
    </div>
  );
}

function ResourceTypeSection({
  query,
  onChanged,
  onToggle,
  toggling,
}: {
  query: ReturnType<typeof useQuery<ResourceTypeResponse[]>>;
  onChanged: (message: string) => void;
  onToggle: (type: ResourceTypeResponse) => void;
  toggling: boolean;
}) {
  const { t } = useTranslation();
  const describeError = useApiErrorMessage();

  const [editing, setEditing] = useState<ResourceTypeResponse | 'new' | null>(null);
  const [name, setName] = useState('');
  const [turnaround, setTurnaround] = useState('15');

  const save = useMutation({
    mutationFn: () => {
      const input = { name, bufferMinutes: Number(turnaround) };

      return editing && editing !== 'new'
        ? updateResourceType(editing.id, input)
        : createResourceType(input);
    },
    onSuccess: () => {
      onChanged(editing === 'new' ? t('catalog.created') : t('catalog.updated'));
      setEditing(null);
    },
  });

  function open(target: ResourceTypeResponse | 'new') {
    save.reset();
    setName(target === 'new' ? '' : target.name);
    setTurnaround(target === 'new' ? '15' : String(target.bufferMinutes));
    setEditing(target);
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t('catalog.resourceTypesSection')}</CardTitle>
        <CardDescription>{t('catalog.resourceTypesNote')}</CardDescription>
      </CardHeader>

      <div className="space-y-4">
        <Button onClick={() => open('new')}>{t('catalog.addResourceType')}</Button>

        {query.isPending ? (
          <p role="status" className="text-meta">
            {t('common.loading')}
          </p>
        ) : query.isError ? (
          <Alert tone="error">{describeError(query.error)}</Alert>
        ) : query.data && query.data.length > 0 ? (
          <Table>
            <TableHead>
              <TableRow>
                <TableHeaderCell>{t('catalog.name')}</TableHeaderCell>
                <TableHeaderCell>{t('catalog.turnaround')}</TableHeaderCell>
                <TableHeaderCell>{t('catalog.columnStatus')}</TableHeaderCell>
                <TableHeaderCell>{t('catalog.columnActions')}</TableHeaderCell>
              </TableRow>
            </TableHead>
            <tbody>
              {query.data.map((type) => (
                <TableRow key={type.id}>
                  <TableCell className="font-medium">{type.name}</TableCell>
                  {/* Measured value, so the mono face with tabular figures. */}
                  <TableCell className="font-mono tabular-nums">{type.bufferMinutes}</TableCell>
                  <StatusCell isActive={type.isActive} />
                  <ActionsCell
                    isActive={type.isActive}
                    onEdit={() => open(type)}
                    onToggle={() => onToggle(type)}
                    busy={toggling}
                  />
                </TableRow>
              ))}
            </tbody>
          </Table>
        ) : (
          <p className="text-meta">{t('catalog.empty')}</p>
        )}
      </div>

      <Dialog open={editing !== null} onOpenChange={(next) => !next && setEditing(null)}>
        {editing !== null ? (
          <DialogContent
            title={editing === 'new' ? t('catalog.addResourceType') : t('catalog.editResourceType')}
          >
            <form
              className="space-y-5"
              onSubmit={(event) => {
                event.preventDefault();
                save.mutate();
              }}
            >
              <Field label={t('catalog.name')}>
                {({ id, describedBy, invalid }) => (
                  <Input
                    id={id}
                    aria-describedby={describedBy}
                    aria-invalid={invalid}
                    value={name}
                    onChange={(event) => setName(event.target.value)}
                    required
                    autoFocus
                  />
                )}
              </Field>

              <Field label={t('catalog.turnaround')} hint={t('catalog.turnaroundHint')}>
                {({ id, describedBy, invalid }) => (
                  <Input
                    id={id}
                    type="number"
                    min={0}
                    aria-describedby={describedBy}
                    aria-invalid={invalid}
                    value={turnaround}
                    onChange={(event) => setTurnaround(event.target.value)}
                    required
                  />
                )}
              </Field>

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
    </Card>
  );
}

function ResourceSection({
  query,
  activeTypes,
  onChanged,
  onToggle,
  toggling,
}: {
  query: ReturnType<typeof useQuery<ResourceResponse[]>>;
  activeTypes: readonly ResourceTypeResponse[];
  onChanged: (message: string) => void;
  onToggle: (resource: ResourceResponse) => void;
  toggling: boolean;
}) {
  const { t } = useTranslation();
  const describeError = useApiErrorMessage();

  const [editing, setEditing] = useState<ResourceResponse | 'new' | null>(null);
  const [name, setName] = useState('');
  const [resourceTypeId, setResourceTypeId] = useState('');

  const save = useMutation({
    mutationFn: () => {
      const input = { name, resourceTypeId };

      return editing && editing !== 'new'
        ? updateResource(editing.id, input)
        : createResource(input);
    },
    onSuccess: () => {
      onChanged(editing === 'new' ? t('catalog.created') : t('catalog.updated'));
      setEditing(null);
    },
  });

  function open(target: ResourceResponse | 'new') {
    save.reset();
    setName(target === 'new' ? '' : target.name);
    setResourceTypeId(target === 'new' ? (activeTypes[0]?.id ?? '') : target.resourceTypeId);
    setEditing(target);
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t('catalog.resourcesSection')}</CardTitle>
        <CardDescription>{t('catalog.resourcesNote')}</CardDescription>
      </CardHeader>

      <div className="space-y-4">
        {/*
          Nothing can be added until an active type exists, so the button says so rather than
          opening a form whose only choice is empty.
        */}
        {activeTypes.length === 0 ? (
          <Alert tone="info">{t('catalog.needsActiveResourceType')}</Alert>
        ) : (
          <Button onClick={() => open('new')}>{t('catalog.addResource')}</Button>
        )}

        {query.isPending ? (
          <p role="status" className="text-meta">
            {t('common.loading')}
          </p>
        ) : query.isError ? (
          <Alert tone="error">{describeError(query.error)}</Alert>
        ) : query.data && query.data.length > 0 ? (
          <Table>
            <TableHead>
              <TableRow>
                <TableHeaderCell>{t('catalog.name')}</TableHeaderCell>
                <TableHeaderCell>{t('catalog.resourceType')}</TableHeaderCell>
                <TableHeaderCell>{t('catalog.columnStatus')}</TableHeaderCell>
                <TableHeaderCell>{t('catalog.columnActions')}</TableHeaderCell>
              </TableRow>
            </TableHead>
            <tbody>
              {query.data.map((resource) => (
                <TableRow key={resource.id}>
                  <TableCell className="font-medium">{resource.name}</TableCell>
                  <TableCell>{resource.resourceTypeName}</TableCell>
                  <StatusCell isActive={resource.isActive} />
                  <ActionsCell
                    isActive={resource.isActive}
                    onEdit={() => open(resource)}
                    onToggle={() => onToggle(resource)}
                    busy={toggling}
                  />
                </TableRow>
              ))}
            </tbody>
          </Table>
        ) : (
          <p className="text-meta">{t('catalog.empty')}</p>
        )}
      </div>

      <Dialog open={editing !== null} onOpenChange={(next) => !next && setEditing(null)}>
        {editing !== null ? (
          <DialogContent
            title={editing === 'new' ? t('catalog.addResource') : t('catalog.editResource')}
          >
            <form
              className="space-y-5"
              onSubmit={(event) => {
                event.preventDefault();
                save.mutate();
              }}
            >
              <Field label={t('catalog.name')}>
                {({ id, describedBy, invalid }) => (
                  <Input
                    id={id}
                    aria-describedby={describedBy}
                    aria-invalid={invalid}
                    value={name}
                    onChange={(event) => setName(event.target.value)}
                    required
                    autoFocus
                  />
                )}
              </Field>

              {/* Only active types are offered — a retired type is not a valid target. */}
              <Field label={t('catalog.resourceType')}>
                {({ id, describedBy }) => (
                  <Select
                    id={id}
                    aria-describedby={describedBy}
                    value={resourceTypeId}
                    onChange={(event) => setResourceTypeId(event.target.value)}
                    required
                  >
                    {activeTypes.map((type) => (
                      <option key={type.id} value={type.id}>
                        {type.name}
                      </option>
                    ))}
                  </Select>
                )}
              </Field>

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
    </Card>
  );
}
