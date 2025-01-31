﻿/*
 *                     GNU AFFERO GENERAL PUBLIC LICENSE
 *                       Version 3, 19 November 2007
 *  Copyright (C) 2007 Free Software Foundation, Inc. <https://fsf.org/>
 *  Everyone is permitted to copy and distribute verbatim copies
 *  of this license document, but changing it is not allowed.
 */

using System;
using System.Threading.Tasks;
using Emgu.CV.Cuda;

namespace UVtools.Core
{
    public static class CoreSettings
    {
        #region Members
        private static int _maxDegreeOfParallelism = -1;

        #endregion

        #region Properties

        /// <summary>
        /// Gets or sets the maximum number of concurrent tasks enabled by this ParallelOptions instance.
        /// Less or equal to 0 will set to auto number
        /// 1 = Single thread
        /// n = Multi threads
        /// </summary>
        public static int MaxDegreeOfParallelism
        {
            get => _maxDegreeOfParallelism;
            set => _maxDegreeOfParallelism = value > 0 ? Math.Min(value, Environment.ProcessorCount) : -1;
        }

        /// <summary>
        /// Gets the ParallelOptions with <see cref="MaxDegreeOfParallelism"/> set
        /// </summary>
        public static ParallelOptions ParallelOptions => new() {MaxDegreeOfParallelism = _maxDegreeOfParallelism};

        /// <summary>
        /// Gets or sets if operations run via cuda when possible
        /// </summary>
        public static bool EnableCuda { get; set; }

        /// <summary>
        /// Gets if we can use cuda on operations
        /// </summary>
        public static bool CanUseCuda => EnableCuda && CudaInvoke.HasCuda;

        #endregion
    }
}
