/*
    Copyright (c) 2009-2011 250bpm s.r.o.
    Copyright (c) 2007-2009 iMatix Corporation
    Copyright (c) 2007-2011 Other contributors as noted in the AUTHORS file

    This file is part of 0MQ.

    0MQ is free software; you can redistribute it and/or modify it under
    the terms of the GNU Lesser General Public License as published by
    the Free Software Foundation; either version 3 of the License, or
    (at your option) any later version.

    0MQ is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU Lesser General Public License for more details.

    You should have received a copy of the GNU Lesser General Public License
    along with this program.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;

namespace NetMQ.zmq.Utils
{
    internal class Poller : PollerBase
    {
        /// <summary>
        /// A PollSet contains a Socket and an IPollEvents Handler
        /// that provides methods that signal when that socket is ready for reading or writing.
        /// </summary>
        private class PollSet
        {
            /// <summary>
            /// Get the Socket that this PollSet contains.
            /// </summary>
            public Socket Socket { get; private set; }

            /// <summary>
            /// Get the IPollEvents object that has methods to signal when ready for reading or writing.
            /// </summary>
            public IPollEvents Handler { get; private set; }

            /// <summary>
            /// Get or set whether this PollSet is cancelled.
            /// </summary>
            public bool Cancelled { get; set; }

            /// <summary>
            /// Create a new PollSet object to hold the given Socket and IPollEvents handler.
            /// </summary>
            /// <param name="socket">the Socket to contain</param>
            /// <param name="handler">the IPollEvents to signal when ready for reading or writing</param>
            public PollSet(Socket socket, IPollEvents handler)
            {
                Handler = handler;
                Socket = socket;
                Cancelled = false;
            }
        }

        /// <summary>
        /// This is the list of registered descriptors (PollSets).
        /// </summary>
        private readonly List<PollSet> m_handles;

        /// <summary>
        /// List of sockets to add at the start of the next loop
        /// </summary>
        private readonly List<PollSet> m_addList;

        /// <summary>
        /// If true, there's at least one retired event source.
        /// </summary>
        private bool m_retired;

        /// <summary>
        /// This flag is used to tell the polling-loop thread to shut down,
        /// wherein it will stop at the end of it's current loop iteration.
        /// </summary>
        volatile private bool m_stopping;

        /// <summary>
        /// This indicates whether the polling-thread is not presently running. Default is true.
        /// </summary>
        volatile private bool m_stopped = true;

        /// <summary>
        /// This is the background-thread that performs the polling-loop.
        /// </summary>
        private Thread m_workerThread;

        /// <summary>
        /// This is the name associated with this Poller.
        /// </summary>
        private readonly String m_name;

        private readonly HashSet<Socket> m_checkRead = new HashSet<Socket>();
        private readonly HashSet<Socket> m_checkWrite = new HashSet<Socket>();
        private readonly HashSet<Socket> m_checkError = new HashSet<Socket>();

        /// <summary>
        /// Create a new Poller object with the default name "poller".
        /// </summary>
        public Poller()
            : this("poller")
        {
        }

        /// <summary>
        /// Create a new Poller object with the given name.
        /// </summary>
        /// <param name="name">a name to assign to this Poller</param>
        public Poller(String name)
        {
            m_name = name;

            m_handles = new List<PollSet>();
            m_addList = new List<PollSet>();
        }

        /// <summary>
        /// Unless the polling-loop is already stopped,
        /// tell it to stop at the end of the current polling iteration, and wait for that thread to finish.
        /// </summary>
        public void Destroy()
        {
            if (!m_stopped)
            {
                try
                {
                    m_stopping = true;
                    m_workerThread.Join();
                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>
        /// Add a new PollSet containing the given Socket and IPollEvents at the next iteration through the loop,
        /// and also add the Socket to the list of those to check for errors.
        /// </summary>
        /// <param name="handle">the Socket to add</param>
        /// <param name="events">the IPollEvents to include in the new PollSet to add</param>
        public void AddHandle(Socket handle, IPollEvents events)
        {
            m_addList.Add(new PollSet(handle, events));

            m_checkError.Add(handle);

            AdjustLoad(1);
        }

        public void RemoveHandle(Socket handle)
        {
            PollSet pollSet;

            // if the socket was removed before being added there is no reason to mark retired, so just cancelling the socket and removing from add list 
            if ((pollSet = m_addList.FirstOrDefault(p => p.Socket == handle)) != null)
            {
                m_addList.Remove(pollSet);
                pollSet.Cancelled = true;
            }
            else
            {
                pollSet = m_handles.First(p => p.Socket == handle);
                pollSet.Cancelled = true;

                m_retired = true;
            }

            m_checkError.Remove(handle);
            m_checkRead.Remove(handle);
            m_checkWrite.Remove(handle);

            //  Decrease the load metric of the thread.
            AdjustLoad(-1);
        }

        /// <summary>
        /// Add the given Socket to the list to be checked for read-readiness at each poll-iteration.
        /// </summary>
        /// <param name="handle">the Socket to add</param>
        public void SetPollin(Socket handle)
        {
            m_checkRead.Add(handle);
        }

        /// <summary>
        /// Remove the given Socket from the list to be checked for read-readiness at each poll iteration.
        /// </summary>
        /// <param name="handle">the Socket to remove</param>
        public void ResetPollin(Socket handle)
        {
            m_checkRead.Remove(handle);
        }

        /// <summary>
        /// Add the given Socket to the list to be checked for write-readiness at each poll-iteration.
        /// </summary>
        /// <param name="handle">the Socket to add</param>
        public void SetPollout(Socket handle)
        {
            m_checkWrite.Add(handle);
        }

        /// <summary>
        /// Remove the given Socket from the list to be checked for write-readiness at each poll iteration.
        /// </summary>
        /// <param name="handle">the Socket to remove</param>
        public void ResetPollout(Socket handle)
        {
            m_checkWrite.Remove(handle);
        }

        /// <summary>
        /// Begin running the polling-loop, on a background thread.
        /// </summary>
        /// <remarks>
        /// The name of that background-thread is the same as the name of this Poller object.
        /// </remarks>
        public void Start()
        {
            m_workerThread = new Thread(Loop);
            m_workerThread.IsBackground = true;
            m_workerThread.Name = m_name;
            m_workerThread.Start();
            m_stopped = false;
        }

        /// <summary>
        /// Signal that we want to stop the polling-loop.
        /// This method returns immediately - it does not wait for the polling thread to stop.
        /// </summary>
        public void Stop()
        {
            m_stopping = true;
        }

        /// <summary>
        /// This method is the polling-loop that is invoked on a background thread when Start is called.
        /// As long as Stop hasn't been called: execute the timers, and invoke the handler-methods on each of the saved PollSets.
        /// </summary>
        private void Loop()
        {
            List<Socket> readList = new List<Socket>();
            List<Socket> writeList = new List<Socket>();
            List<Socket> errorList = new List<Socket>();

            while (!m_stopping)
            {
                // Transfer any sockets from the add-list.
                m_handles.AddRange(m_addList);
                m_addList.Clear();

                // Execute any due timers.
                int timeout = ExecuteTimers();

                readList.AddRange(m_checkRead.ToArray());
                writeList.AddRange(m_checkWrite.ToArray());
                errorList.AddRange(m_checkError.ToArray());

                try
                {
                    SocketUtility.Select(readList, writeList, errorList, timeout != 0 ? timeout * 1000 : -1);
                }
                catch (SocketException)
                {
                    continue;
                }

                // For every PollSet in our list..
                foreach (var pollSet in m_handles)
                {
                    if (pollSet.Cancelled)
                    {
                        continue;
                    }

                    // Invoke it's handler's InEvent if it's in our error-list.
                    if (errorList.Contains(pollSet.Socket))
                    {
                        try
                        {
                            pollSet.Handler.InEvent();
                        }
                        catch (TerminatingException)
                        {
                        }
                    }

                    if (pollSet.Cancelled)
                    {
                        continue;
                    }

                    // Invoke it's handler's OutEvent if it's in our write-list.
                    if (writeList.Contains(pollSet.Socket))
                    {
                        try
                        {
                            pollSet.Handler.OutEvent();
                        }
                        catch (TerminatingException)
                        {
                        }
                    }

                    if (pollSet.Cancelled)
                    {
                        continue;
                    }

                    // Invoke it's handler's InEvent if it's in our read-list.
                    if (readList.Contains(pollSet.Socket))
                    {
                        try
                        {
                            pollSet.Handler.InEvent();
                        }
                        catch (TerminatingException)
                        {
                        }
                    }
                }

                errorList.Clear();
                writeList.Clear();
                readList.Clear();

                if (m_retired)
                {
                    // Take any sockets that have been cancelled out of the list..
                    foreach (var item in m_handles.Where(k => k.Cancelled).ToList())
                    {
                        m_handles.Remove(item);
                    }

                    m_retired = false;
                }
            }
            m_stopped = true;
        }
    }
}
